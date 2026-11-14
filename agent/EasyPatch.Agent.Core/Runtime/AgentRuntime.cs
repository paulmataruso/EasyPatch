using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Agent.Core.Enrollment;
using EasyPatch.Agent.Core.Jobs;
using EasyPatch.Agent.Core.Outbox;
using EasyPatch.Agent.Core.Policy;
using EasyPatch.Agent.Core.Scanning;
using EasyPatch.Agent.Core.Security;
using EasyPatch.Agent.Core.State;
using EasyPatch.Agent.Core.Util;
using EasyPatch.Communication;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasyPatch.Agent.Core.Runtime;

/// <summary>
/// The EasyPatch agent. <see cref="RunAsync"/> is the long-running service loop; the single-step methods
/// (<see cref="EnsureEnrolledAsync"/>, <see cref="CheckInOnceAsync"/>, <see cref="ProcessNextJobAsync"/>,
/// <see cref="RunScanAsync"/>, <see cref="SendInventoryAsync"/>, <see cref="FlushOutboxAsync"/>) drive the same
/// logic deterministically for tests and integration scenarios.
/// </summary>
public sealed class AgentRuntime : IDisposable
{
    private readonly AgentRuntimeOptions _options;
    private readonly AgentPlatform _platform;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly AgentKeyStore _keys;
    private readonly ReportOutbox _outbox;
    private readonly WuaOperationGate _gate = new();
    private readonly ApiClientAccessor _api;
    private readonly EnrollmentManager _enrollment;
    private readonly ScanService _scans;
    private readonly InventoryService _inventory;
    private readonly JobRunner _jobs;
    private readonly ReleaseKeyRing _releaseKeys;
    private readonly AgentUpdateHandler _agentUpdate;
    private readonly WindowsUpdatePolicyManager? _policy;
    private readonly SemaphoreSlim _flushSignal = new(0, int.MaxValue);
    private string? _announcedVersion;
    private bool _rebootPending;

    public AgentRuntime(
        AgentRuntimeOptions options,
        AgentPlatform platform,
        IAgentHttpClientFactory httpClientFactory,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrEmpty(options.DataDirectory);
        ArgumentException.ThrowIfNullOrEmpty(options.AgentVersion);

        loggerFactory ??= NullLoggerFactory.Instance;
        _options = options;
        _platform = platform;
        _time = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<AgentRuntime>();

        Directory.CreateDirectory(options.DataDirectory);
        State = new AgentStateStore(options.DataDirectory, loggerFactory.CreateLogger<AgentStateStore>());
        _keys = new AgentKeyStore(options.DataDirectory, platform.SecretProtector, loggerFactory.CreateLogger<AgentKeyStore>());
        _outbox = new ReportOutbox(options.DataDirectory, options.MaxOutboxItems, options.MaxOutboxBytes, _time, loggerFactory.CreateLogger<ReportOutbox>());
        _outbox.ItemEnqueued += (_, _) => _flushSignal.Release();

        var clock = new ServerClock(_time, TimeSpan.FromSeconds(State.Read(s => s.ServerClockOffsetSeconds)));
        clock.OffsetChanged += (_, offset) => State.Update(s => s.ServerClockOffsetSeconds = offset.TotalSeconds);
        _api = new ApiClientAccessor(httpClientFactory, new StateSigningCredentialProvider(State, _keys), clock,
            options.AgentVersion, options.RequireHttps, loggerFactory);

        _enrollment = new EnrollmentManager(State, _keys, platform, _api, _outbox, options, _time, loggerFactory.CreateLogger<EnrollmentManager>());
        _scans = new ScanService(platform, State, _outbox, _gate, options, _time, loggerFactory.CreateLogger<ScanService>());
        _inventory = new InventoryService(platform, State, _outbox, _scans, options, _time, loggerFactory.CreateLogger<InventoryService>());
        var reporter = new JobReporter(State, _outbox);
        var notifier = new UserNotifier(platform, State, loggerFactory.CreateLogger<UserNotifier>());
        var install = new InstallUpdatesHandler(platform, State, reporter, _scans, notifier, _time, loggerFactory.CreateLogger<InstallUpdatesHandler>());
        var restart = new RestartHandler(platform, State, reporter, options, _time, loggerFactory.CreateLogger<RestartHandler>());
        _releaseKeys = LoadReleaseKeys(options.ReleasePublicKeys);
        _agentUpdate = new AgentUpdateHandler(platform, State, reporter, _releaseKeys, GetActiveClient, options, _time,
            loggerFactory.CreateLogger<AgentUpdateHandler>());
        _jobs = new JobRunner(platform, State, reporter, _scans, _inventory, install, restart, _agentUpdate, notifier, _gate, options, _time,
            loggerFactory.CreateLogger<JobRunner>());
        if (platform.PolicyRegistry is not null)
        {
            _policy = new WindowsUpdatePolicyManager(platform.PolicyRegistry, State, _time, loggerFactory.CreateLogger<WindowsUpdatePolicyManager>());
        }

        State.Update(s => s.FirstStartedAt ??= _time.GetUtcNow());
    }

    /// <summary>Convenience constructor: every request goes through <paramref name="httpClient"/> (its BaseAddress is the server).</summary>
    public AgentRuntime(
        AgentRuntimeOptions options,
        AgentPlatform platform,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
        : this(options, platform, new FixedHttpClientFactory(httpClient), loggerFactory, timeProvider)
    {
    }

    public AgentStateStore State { get; }

    public ReportOutbox Outbox => _outbox;

    public ServerClock Clock => _api.Clock;

    public string CurrentActivity => _jobs.CurrentActivity;

    // ================================================================== single-step operations

    /// <summary>Enrolls (token or deployment code) or polls a pending approval. Idempotent once active.</summary>
    public Task<EnrollmentOutcome> EnsureEnrolledAsync(CancellationToken cancellationToken = default) =>
        _enrollment.EnsureEnrolledAsync(cancellationToken);

    /// <summary>
    /// Flushes queued reports (best effort), then checks in: sends heartbeat state, persists and acknowledges any
    /// delivered jobs, applies cancellations and rotates the credential when asked. Returns null when not enrolled.
    /// </summary>
    public async Task<CheckInResponse?> CheckInOnceAsync(CancellationToken cancellationToken = default)
    {
        var client = GetActiveClient();
        if (client is null)
        {
            return null;
        }

        try
        {
            await _outbox.FlushAsync(client, cancellationToken);
        }
        catch (AgentTransientException)
        {
            // Check in anyway; the flush will be retried.
        }
        catch (AgentAuthException ex) when (ex.IsFatal)
        {
            HandleFatalAuth(ex);
            throw;
        }

        var reboot = await DetectRebootAsync(cancellationToken);
        _rebootPending = reboot.Pending;
        var held = _jobs.HeldJobIds().Union(_outbox.PendingJobIds()).ToList();
        var policyReport = _policy?.PendingReport();
        var request = new CheckInRequest
        {
            AgentVersion = _options.AgentVersion,
            LastBootAt = _platform.BootTime.GetLastBootTime(),
            RebootPending = reboot.Pending,
            RebootReasons = reboot.Reasons,
            ActiveJobIds = held,
            CurrentActivity = _jobs.CurrentActivity,
            Capabilities = _agentUpdate.CanUpdate ? [.. _platform.Capabilities, AgentCapabilities.SelfUpdate] : _platform.Capabilities,
            AgentArchitecture = _options.PackageArchitecture,
            ReleaseKeyIds = _agentUpdate.TrustedKeyIds,
            AgentPackageFormat = _platform.AgentInstaller?.PackageFormat,
            Sessions = _platform.UserInteraction is null ? null : SafeSessions(),
            WindowsUpdatePolicy = policyReport,
        };

        CheckInResponse response;
        try
        {
            response = await client.CheckInAsync(request, cancellationToken);
        }
        catch (AgentAuthException ex) when (ex.IsFatal)
        {
            HandleFatalAuth(ex);
            throw;
        }

        var config = AgentConfigLimits.Normalize(response.Config);
        State.Update(s =>
        {
            s.LastCheckInAt = _time.GetUtcNow();
            s.Config = config;
            s.LastAuthFailureCode = null;
            s.LastAuthFailureAt = null;
        });

        if (_policy is not null)
        {
            if (policyReport is not null)
            {
                _policy.MarkReported(policyReport);
            }

            _policy.Apply(config.WindowsUpdate);
        }

        var toAcknowledge = _jobs.Accept(response.Jobs);
        foreach (var jobId in toAcknowledge)
        {
            await AcknowledgeAsync(client, jobId, cancellationToken);
        }

        _jobs.DispatchMessages();
        PublishTrayStatus();

        foreach (var jobId in response.CancelJobIds)
        {
            _jobs.RequestCancel(jobId);
        }

        if (response.RotateCredential)
        {
            try
            {
                await RotateCredentialAsync(cancellationToken);
            }
            catch (AgentTransientException ex)
            {
                _logger.LogWarning("Credential rotation will be retried: {Message}", ex.Message);
            }
        }

        AnnounceLatestVersion(response.LatestAgent);
        return response;
    }

    /// <summary>Executes the next runnable job (one at a time). Returns true if a job was processed.</summary>
    public Task<bool> ProcessNextJobAsync(CancellationToken cancellationToken = default) =>
        _jobs.ProcessNextAsync(cancellationToken);

    /// <summary>Runs a scan and queues the result (plus history). Returns the scan ID, or null if skipped.</summary>
    public async Task<Guid?> RunScanAsync(ScanTrigger trigger = ScanTrigger.Scheduled, CancellationToken cancellationToken = default)
    {
        var report = await _scans.RunScanAsync(trigger, jobId: null, gateHeld: false, cancellationToken);
        return report?.ScanId;
    }

    /// <summary>Collects inventory and queues it.</summary>
    public Task SendInventoryAsync(CancellationToken cancellationToken = default) =>
        _inventory.SendInventoryAsync(cancellationToken);

    /// <summary>Delivers queued reports. Returns true when the outbox is empty afterwards.</summary>
    public async Task<bool> FlushOutboxAsync(CancellationToken cancellationToken = default)
    {
        var client = GetActiveClient();
        if (client is null)
        {
            return _outbox.Count == 0;
        }

        try
        {
            return await _outbox.FlushAsync(client, cancellationToken) == FlushResult.Drained;
        }
        catch (AgentAuthException ex) when (ex.IsFatal)
        {
            HandleFatalAuth(ex);
            throw;
        }
    }

    /// <summary>Registers a new key with the server (signed with the current key), then switches to it.</summary>
    public async Task RotateCredentialAsync(CancellationToken cancellationToken = default)
    {
        var client = GetActiveClient() ?? throw new InvalidOperationException("The agent is not enrolled.");
        var pending = _keys.GetOrCreatePending();
        var response = await client.RotateCredentialAsync(new RotateCredentialRequest { PublicKey = AgentKeyStore.PublicKeyBase64(pending) }, cancellationToken);
        var fingerprint = AgentKeyStore.Fingerprint(pending);

        // Save the new credential first, then promote the key: an interruption in between is repaired on startup
        // by AgentKeyStore.EnsureMatches (the state fingerprint identifies the pending key).
        State.Update(s =>
        {
            s.CredentialId = response.CredentialId;
            s.CredentialExpiresAt = response.ExpiresAt;
            s.KeyFingerprint = fingerprint;
        });
        _keys.PromotePending();
        _logger.LogInformation(AgentEventIds.CredentialRotated, "Credential rotated; new credential {CredentialId} (fingerprint {Fingerprint}) expires {ExpiresAt:u}.",
            response.CredentialId, fingerprint, response.ExpiresAt);
    }

    /// <summary>Best-effort server notification, restoration of any Windows Update settings EasyPatch changed, then
    /// deletion of all local identity (MSI uninstall).</summary>
    public async Task UnenrollAsync(string? reason, CancellationToken cancellationToken = default)
    {
        RestoreWindowsUpdatePolicy();
        var client = GetActiveClient();
        if (client is not null)
        {
            try
            {
                await client.UnenrollAsync(new UnenrollRequest { Reason = reason }, cancellationToken);
                _logger.LogInformation("The server was notified that this agent is being removed.");
            }
            catch (AgentApiException ex)
            {
                _logger.LogWarning("Could not notify the server about unenrollment: {Message}", ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Uninstall must not hang on an unreachable server; the local identity is removed regardless.
                _logger.LogWarning("Timed out notifying the server about unenrollment.");
            }
        }

        _enrollment.ResetIdentity();
        State.Reset();
    }

    // ================================================================== service loop

    /// <summary>Runs until <paramref name="stoppingToken"/> is cancelled. Restarts the computer only for an administrator's
    /// restart job.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var snapshot = State.Snapshot();
        _logger.LogInformation("EasyPatch agent {Version} starting (status {Status}, data {DataDirectory}).",
            _options.AgentVersion, snapshot.Status, _options.DataDirectory);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnrollmentLoopAsync(stoppingToken);
                await RunActiveSessionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected agent error; restarting the main loop in one minute.");
                await DelayAsync(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("EasyPatch agent stopping.");
    }

    private async Task EnrollmentLoopAsync(CancellationToken stoppingToken)
    {
        var backoff = new ExponentialBackoff(_options.BackoffBase, _options.BackoffMax);
        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            try
            {
                var outcome = await _enrollment.EnsureEnrolledAsync(stoppingToken);
                var revoked = State.Read(s => s.Status == AgentEnrollmentStatus.Revoked);
                switch (outcome)
                {
                    case EnrollmentOutcome.Active:
                        return;
                    case EnrollmentOutcome.PendingApproval:
                        backoff.Reset();
                        delay = Jitter.Apply(_options.PendingApprovalPollInterval, 10);
                        break;
                    case EnrollmentOutcome.NotConfigured:
                        delay = revoked ? _options.RevokedRetryInterval : _options.NotConfiguredRetryInterval;
                        break;
                    case EnrollmentOutcome.Rejected:
                        delay = _options.RevokedRetryInterval;
                        break;
                    default:
                        delay = backoff.NextDelay(_options.NotConfiguredRetryInterval);
                        break;
                }
            }
            catch (AgentTransientException ex)
            {
                delay = backoff.NextDelay(ex.RetryAfter);
                _logger.LogWarning("Enrollment could not reach the server; retrying in {Delay}: {Message}", delay, ex.Message);
            }
            catch (AgentAuthException ex)
            {
                delay = backoff.NextDelay(_options.NotConfiguredRetryInterval);
                _logger.LogWarning("Enrollment status check was rejected ({Code}); retrying in {Delay}.", ex.ErrorCode, delay);
            }

            await DelayAsync(delay, stoppingToken);
        }
    }

    private async Task RunActiveSessionAsync(CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var communication = CommunicationLoopAsync(session);
        var work = WorkLoopAsync(session.Token);
        await Task.WhenAny(communication, work);
        await session.CancelAsync();
        try
        {
            await Task.WhenAll(communication, work);
        }
        catch (OperationCanceledException)
        {
        }

        stoppingToken.ThrowIfCancellationRequested();
        if (State.Read(s => s.Status) == AgentEnrollmentStatus.Revoked)
        {
            await DelayAsync(_options.RevokedRetryInterval, stoppingToken);
        }
    }

    private async Task CommunicationLoopAsync(CancellationTokenSource session)
    {
        var token = session.Token;
        var backoff = new ExponentialBackoff(_options.BackoffBase, _options.BackoffMax);
        var nextCheckIn = _time.GetUtcNow();
        while (!token.IsCancellationRequested)
        {
            var wait = nextCheckIn - _time.GetUtcNow();
            var flushRequested = false;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    flushRequested = await _flushSignal.WaitAsync(wait, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            try
            {
                if (flushRequested && _time.GetUtcNow() < nextCheckIn && backoff.Attempt == 0)
                {
                    DrainSignal();
                    await FlushOutboxAsync(token);
                    continue;
                }

                DrainSignal();
                await CheckInOnceAsync(token);
                backoff.Reset();
                var config = State.Read(s => s.Config);
                var interval = _jobs.HasRunnableOrActiveJob || _outbox.Count > 0
                    ? TimeSpan.FromSeconds(config.ActiveJobCheckInIntervalSeconds)
                    : TimeSpan.FromSeconds(config.CheckInIntervalSeconds);
                nextCheckIn = _time.GetUtcNow() + Jitter.Apply(interval, config.JitterPercent);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (AgentTransientException ex)
            {
                var delay = backoff.NextDelay(ex.RetryAfter);
                nextCheckIn = _time.GetUtcNow() + delay;
                _logger.LogWarning("Server unreachable; retrying in {Delay:g}. {Message}", delay, ex.Message);
            }
            catch (AgentAuthException ex) when (ex.IsFatal)
            {
                HandleFatalAuth(ex);
                await session.CancelAsync();
                return;
            }
            catch (AgentAuthException ex)
            {
                var delay = backoff.NextDelay();
                nextCheckIn = _time.GetUtcNow() + delay;
                State.Update(s =>
                {
                    s.LastAuthFailureCode = ex.ErrorCode;
                    s.LastAuthFailureAt = _time.GetUtcNow();
                });
                _logger.LogError("The server rejected a request ({Code}); retrying in {Delay:g}.", ex.ErrorCode, delay);
            }
            catch (Exception ex)
            {
                var delay = backoff.NextDelay();
                nextCheckIn = _time.GetUtcNow() + delay;
                _logger.LogError(ex, "Check-in failed; retrying in {Delay:g}.", delay);
            }
        }
    }

    private async Task WorkLoopAsync(CancellationToken token)
    {
        var snapshot = State.Snapshot();
        var now = _time.GetUtcNow();
        var boot = _platform.BootTime.GetLastBootTime();
        var scanInterval = TimeSpan.FromMinutes(snapshot.Config.ScanIntervalMinutes);
        var inventoryInterval = TimeSpan.FromMinutes(snapshot.Config.InventoryIntervalMinutes);

        var rebooted = snapshot.LastBootAt is { } lastBoot && (boot - lastBoot).Duration() > _options.BootTimeTolerance;
        State.Update(s => s.LastBootAt = boot);

        var scanTrigger = ScanTrigger.Scheduled;
        DateTimeOffset scanDue;
        if (rebooted)
        {
            scanTrigger = ScanTrigger.PostReboot;
            scanDue = now + Jitter.Between(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(3));
        }
        else if (snapshot.LastScanAttemptAt is not { } lastScan || now - lastScan > scanInterval)
        {
            scanTrigger = ScanTrigger.Startup;
            scanDue = now + Jitter.Between(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3));
        }
        else
        {
            scanDue = lastScan + Jitter.Apply(scanInterval, snapshot.Config.JitterPercent);
        }

        var inventoryDue = snapshot.LastInventoryAt is { } lastInventory && now - lastInventory < inventoryInterval
            ? lastInventory + Jitter.Apply(inventoryInterval, snapshot.Config.JitterPercent)
            : now + Jitter.Between(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));

        while (!token.IsCancellationRequested)
        {
            try
            {
                var processed = await _jobs.ProcessNextAsync(token);
                PublishTrayStatus();
                if (processed)
                {
                    continue;
                }

                var config = State.Read(s => s.Config);
                now = _time.GetUtcNow();
                if (now >= scanDue)
                {
                    var scanId = await RunScanAsync(scanTrigger, token);
                    scanTrigger = ScanTrigger.Scheduled;
                    scanDue = scanId is null
                        ? now + TimeSpan.FromMinutes(5) // skipped because Windows Update was busy
                        : _time.GetUtcNow() + Jitter.Apply(TimeSpan.FromMinutes(config.ScanIntervalMinutes), config.JitterPercent);
                }

                if (now >= inventoryDue)
                {
                    await SendInventoryAsync(token);
                    inventoryDue = _time.GetUtcNow() + Jitter.Apply(TimeSpan.FromMinutes(config.InventoryIntervalMinutes), config.JitterPercent);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agent work loop error.");
                await DelayAsync(TimeSpan.FromSeconds(30), token);
            }

            await DelayAsync(_options.WorkLoopInterval, token);
        }
    }

    // ================================================================== helpers

    private AgentApiClient? GetActiveClient()
    {
        var (status, serverUrl) = State.Read(s => (s.Status, s.ServerUrl));
        if (status != AgentEnrollmentStatus.Active || string.IsNullOrEmpty(serverUrl))
        {
            return null;
        }

        return _api.Get(serverUrl);
    }

    private async Task AcknowledgeAsync(AgentApiClient client, Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            var ack = await client.AcknowledgeJobAsync(jobId, cancellationToken);
            if (ack.Accepted)
            {
                _jobs.MarkAcknowledged(jobId);
            }
            else
            {
                _jobs.Drop(jobId, ack.Reason ?? "the server declined the acknowledgement");
            }
        }
        catch (AgentRejectedException ex)
        {
            _jobs.Drop(jobId, $"the server rejected the acknowledgement ({ex.ErrorCode ?? ((int?)ex.StatusCode)?.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        }
        catch (AgentTransientException ex)
        {
            _logger.LogWarning("Acknowledgement of job {JobId} will be retried: {Message}", jobId, ex.Message);
        }
    }

    private async Task<RebootStatus> DetectRebootAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _platform.RebootDetector.DetectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Reboot detection failed.");
            return RebootStatus.None;
        }
    }

    private void HandleFatalAuth(AgentAuthException ex)
    {
        _logger.LogError(AgentEventIds.CredentialRevoked,
            "The server rejected this agent's credential ({Code}). Normal operation is suspended. Re-enroll the agent with a new enrollment token or deployment code.",
            ex.ErrorCode);
        _outbox.Clear();
        RestoreWindowsUpdatePolicy();
        SafeUserCall(u => u.SetRestartPrompt(null));
        State.Update(s =>
        {
            s.Status = AgentEnrollmentStatus.Revoked;
            s.LastAuthFailureCode = ex.ErrorCode;
            s.LastAuthFailureAt = _time.GetUtcNow();
            s.Jobs.Clear();
        });
    }

    private void RestoreWindowsUpdatePolicy()
    {
        try
        {
            _policy?.RestoreAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restore the original Windows Update settings.");
        }
    }

    private UserSessionInfo? SafeSessions()
    {
        try
        {
            return _platform.Users.GetSessions();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate signed-in users.");
            return null;
        }
    }

    private void PublishTrayStatus()
    {
        if (_platform.UserInteraction is null)
        {
            return;
        }

        var (lastContact, showIcon) = State.Read(s => (s.LastCheckInAt, s.Config.UserExperience.TrayIcon));
        SafeUserCall(u => u.SetStatus(new TrayStatus
        {
            Activity = _jobs.CurrentActivity,
            LastContactAt = lastContact,
            RebootPending = _rebootPending,
            AgentVersion = _options.AgentVersion,
            ShowIcon = showIcon,
        }));
    }

    private void SafeUserCall(Action<IUserInteraction> action)
    {
        try
        {
            action(_platform.Users);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not update the tray.");
        }
    }

    private void AnnounceLatestVersion(AgentVersionInfo? latest)
    {
        if (latest is null || latest.Version == _announcedVersion)
        {
            return;
        }

        _announcedVersion = latest.Version;
        if (Version.TryParse(latest.Version, out var available) &&
            Version.TryParse(_options.AgentVersion.Split('+', '-')[0], out var current) &&
            available > current)
        {
            if (_agentUpdate.CanUpdate)
            {
                _logger.LogInformation("A newer EasyPatch agent ({Available}) is available; this agent is {Current}. It is installed when an administrator updates this agent or the \"Agent updates\" policy is Automatic.",
                    latest.Version, _options.AgentVersion);
            }
            else
            {
                _logger.LogInformation("A newer EasyPatch agent ({Available}) is available; this agent is {Current} and cannot update itself. Install the new MSI to upgrade.",
                    latest.Version, _options.AgentVersion);
            }
        }
    }

    private ReleaseKeyRing LoadReleaseKeys(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            _logger.LogInformation("This agent was built without a release key, so it cannot update itself. Upgrade it by installing a newer MSI.");
            return ReleaseKeyRing.Empty;
        }

        try
        {
            var ring = new ReleaseKeyRing(pem);
            if (ring.IsEmpty)
            {
                _logger.LogError("The release key text built into this agent contains no public key, so it cannot update itself.");
                return ring;
            }

            _logger.LogDebug("Agent updates are accepted when signed with release key(s) {KeyIds}.", string.Join(", ", ring.KeyIds));
            return ring;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            _logger.LogError("The release key built into this agent is not valid, so it cannot update itself: {Message}", ex.Message);
            return ReleaseKeyRing.Empty;
        }
    }

    private void DrainSignal()
    {
        while (_flushSignal.CurrentCount > 0 && _flushSignal.Wait(0))
        {
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken);
        }
    }

    public void Dispose()
    {
        _keys.Dispose();
        _releaseKeys.Dispose();
        _outbox.Dispose();
        _gate.Dispose();
        _flushSignal.Dispose();
    }
}
