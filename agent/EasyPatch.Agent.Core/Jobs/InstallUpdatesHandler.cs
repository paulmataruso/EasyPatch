using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Agent.Core.Scanning;
using EasyPatch.Agent.Core.State;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using EasyPatch.Contracts.Updates;
using Microsoft.Extensions.Logging;

namespace EasyPatch.Agent.Core.Jobs;

public enum JobOutcome
{
    /// <summary>A final status was reported; the job can be forgotten.</summary>
    Finished,

    /// <summary>Parked until the computer restarts.</summary>
    WaitingForReboot,
}

/// <summary>
/// Executes InstallUpdates jobs with the native Windows Update Agent. Idempotent: every run starts with a fresh
/// scan, so re-running after a crash, service restart or reboot never reinstalls what is already installed.
/// </summary>
public sealed class InstallUpdatesHandler
{
    /// <summary>Skip reasons worth showing to administrators for "install all" jobs (policy filtering is not noise-free otherwise).</summary>
    private static readonly HashSet<string> ReportedSkipReasons = new(StringComparer.Ordinal)
    {
        SkipReasons.Excluded,
        SkipReasons.RequiresUserInput,
        SkipReasons.EulaNotAccepted,
    };

    private readonly AgentPlatform _platform;
    private readonly AgentStateStore _state;
    private readonly JobReporter _reporter;
    private readonly ScanService _scans;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public InstallUpdatesHandler(
        AgentPlatform platform,
        AgentStateStore state,
        JobReporter reporter,
        ScanService scans,
        TimeProvider time,
        ILogger<InstallUpdatesHandler> logger)
    {
        _platform = platform;
        _state = state;
        _reporter = reporter;
        _scans = scans;
        _time = time;
        _logger = logger;
    }

    /// <summary>Current human-readable activity (e.g. "Installing KB5044284").</summary>
    public string? Activity { get; private set; }

    public async Task<JobOutcome> ExecuteAsync(JobRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var job = record.Job;
        var jobId = job.JobId;
        var payload = job.Install ?? throw new InvalidOperationException("The install job has no payload.");
        var previous = record.Items.ToDictionary(i => i.UpdateGuid);
        var wu = _platform.WindowsUpdate;

        try
        {
            if (record.WaitingBootTime is not null)
            {
                _logger.LogInformation("Resuming job {JobId} after the computer restarted.", jobId);
                _state.Update(s =>
                {
                    var r = s.FindJob(jobId);
                    if (r is not null)
                    {
                        r.WaitingBootTime = null;
                        r.WaitingSince = null;
                    }
                });
            }

            var anyStarted = previous.Values.Any(i => i.Status is JobItemStatus.Succeeded or JobItemStatus.Failed or JobItemStatus.Installing);
            if (payload.NotAfter is { } notAfter && _time.GetUtcNow() > notAfter && !anyStarted)
            {
                _logger.LogWarning("Job {JobId}: the maintenance window closed before installation started.", jobId);
                _reporter.Report(jobId, JobStatus.Cancelled, "NotStarted", 100,
                    "The maintenance window closed before installation could start.",
                    errorCode: "maintenance_window_closed",
                    errorMessage: "The maintenance window closed before installation could start.");
                return JobOutcome.Finished;
            }

            Activity = "Scanning for updates";
            _reporter.Report(jobId, JobStatus.Running, "Scanning", 0, "Checking Windows Update for applicable updates.", previous.Values.ToList());

            if (await wu.IsRebootRequiredBeforeInstallationAsync(cancellationToken))
            {
                return EnterWaitingForReboot(jobId, "Windows requires a restart before updates can be installed.", previous.Values.ToList());
            }

            using var scan = await wu.ScanAsync(includeInstalled: payload.Mode == InstallMode.Selected, cancellationToken);
            var plan = BuildPlan(scan, payload, previous);
            var items = plan.Items;
            _logger.LogInformation("Job {JobId}: {Count} update(s) to install, {Skipped} skipped.",
                jobId, plan.ToInstall.Count, items.Count(i => i.Status == JobItemStatus.Skipped));

            var cancelled = false;
            for (var index = 0; index < plan.ToInstall.Count; index++)
            {
                var candidate = plan.ToInstall[index];
                var guid = candidate.Update.UpdateGuid;
                var label = Label(candidate.Update);

                if (IsCancelRequested(jobId))
                {
                    cancelled = true;
                    MarkRemaining(items, plan.ToInstall, index, JobItemStatus.Cancelled, SkipReasons.Cancelled);
                    break;
                }

                if (payload.NotAfter is { } windowEnd && _time.GetUtcNow() > windowEnd)
                {
                    MarkRemaining(items, plan.ToInstall, index, JobItemStatus.Skipped, SkipReasons.MaintenanceWindowClosed);
                    _logger.LogWarning("Job {JobId}: maintenance window closed; {Remaining} update(s) not started.", jobId, plan.ToInstall.Count - index);
                    break;
                }

                var basePercent = 5 + (int)(90.0 * index / plan.ToInstall.Count);
                var startedAt = _time.GetUtcNow();

                // ---- download
                if (!candidate.Update.IsDownloaded)
                {
                    Activity = $"Downloading {label}";
                    Set(items, guid, i => i with { Status = JobItemStatus.Downloading, StartedAt = startedAt, ErrorCode = null, ErrorMessage = null });
                    _reporter.Report(jobId, JobStatus.Running, "Downloading", basePercent, $"Downloading {label} ({index + 1} of {plan.ToInstall.Count}).", items);

                    var download = await RunOperationAsync(() => wu.DownloadAsync(candidate.Handle, cancellationToken));
                    if (!download.IsSuccess)
                    {
                        Fail(items, guid, download, "Download failed");
                        _reporter.Report(jobId, JobStatus.Running, "Downloading", basePercent, $"Download of {label} failed.", items);
                        continue;
                    }
                }

                Set(items, guid, i => i with { Status = JobItemStatus.Downloaded, StartedAt = i.StartedAt ?? startedAt, DownloadPercent = 100 });

                // ---- install
                Activity = $"Installing {label}";
                Set(items, guid, i => i with { Status = JobItemStatus.Installing });
                _reporter.Report(jobId, JobStatus.Running, "Installing", basePercent, $"Installing {label} ({index + 1} of {plan.ToInstall.Count}).", items);

                var install = await RunOperationAsync(() => wu.InstallAsync(candidate.Handle, payload.Filter.AcceptEulas, cancellationToken));
                if (install.IsSuccess)
                {
                    Set(items, guid, i => i with
                    {
                        Status = JobItemStatus.Succeeded,
                        ResultCode = install.ResultCode,
                        RebootRequired = install.RebootRequired,
                        ErrorCode = install.ResultCode == UpdateOperationResult.SucceededWithErrors ? WindowsUpdateErrors.Format(install.HResult) : null,
                        ErrorMessage = install.ResultCode == UpdateOperationResult.SucceededWithErrors ? "Installed with errors: " + WindowsUpdateErrors.Describe(install.HResult) : null,
                        CompletedAt = _time.GetUtcNow(),
                    });
                    _logger.LogInformation("Job {JobId}: installed {Update}{Reboot}.", jobId, label, install.RebootRequired ? " (restart required)" : string.Empty);
                }
                else
                {
                    Fail(items, guid, install, "Installation failed");
                }

                _reporter.Report(jobId, JobStatus.Running, "Installing", basePercent + (int)(90.0 / plan.ToInstall.Count),
                    install.IsSuccess ? $"Installed {label}." : $"Installation of {label} failed.", items);

                if (!install.IsSuccess && index < plan.ToInstall.Count - 1 &&
                    await wu.IsRebootRequiredBeforeInstallationAsync(cancellationToken))
                {
                    return EnterWaitingForReboot(jobId, "A restart is required before the remaining updates can be installed.", items);
                }
            }

            // ---- verify with a fresh scan so the server reconciles real state
            Activity = "Verifying installation";
            _reporter.Report(jobId, JobStatus.Running, "Verifying", 96, "Running a post-installation scan.", items);
            var verification = await _scans.RunScanAsync(ScanTrigger.PostInstall, jobId, gateHeld: true, cancellationToken);

            return Finish(jobId, items, plan.ToInstall.Count, cancelled, verification?.ScanId);
        }
        finally
        {
            Activity = null;
        }
    }

    private JobOutcome Finish(Guid jobId, List<JobItemReport> items, int planned, bool cancelled, Guid? scanId)
    {
        var succeeded = items.Count(i => i.Status == JobItemStatus.Succeeded);
        var failed = items.Count(i => i.Status == JobItemStatus.Failed);
        var skipped = items.Count(i => i.Status is JobItemStatus.Skipped or JobItemStatus.Cancelled);
        var rebootRequired = items.Any(i => i.Status == JobItemStatus.Succeeded && i.RebootRequired);
        var summary = new JobResultSummary
        {
            Planned = planned,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            RebootRequired = rebootRequired,
            PostInstallScanId = scanId,
        };

        var status = cancelled
            ? JobStatus.Cancelled
            : planned > 0 && succeeded == 0 && failed > 0 ? JobStatus.Failed : JobStatus.Completed;

        var message = planned == 0
            ? "No updates needed to be installed."
            : $"Installed {succeeded} of {planned} update(s)" + (failed > 0 ? $"; {failed} failed" : string.Empty) + (cancelled ? "; cancelled" : string.Empty) + ".";
        if (rebootRequired)
        {
            message += " A restart is required to complete installation.";
        }

        var firstFailure = items.FirstOrDefault(i => i.Status == JobItemStatus.Failed);
        _reporter.Report(jobId, status, "Completed", 100, message, items,
            errorCode: status == JobStatus.Failed ? firstFailure?.ErrorCode : null,
            errorMessage: status == JobStatus.Failed ? firstFailure?.ErrorMessage : null,
            rebootRequired: rebootRequired,
            result: summary);
        _logger.LogInformation("Job {JobId} finished: {Status}. {Message}", jobId, status, message);
        return JobOutcome.Finished;
    }

    private JobOutcome EnterWaitingForReboot(Guid jobId, string message, IReadOnlyList<JobItemReport> items)
    {
        var bootTime = _platform.BootTime.GetLastBootTime();
        var now = _time.GetUtcNow();
        _state.Update(s =>
        {
            var r = s.FindJob(jobId);
            if (r is not null)
            {
                r.RunState = JobRunState.WaitingForReboot;
                r.WaitingBootTime = bootTime;
                r.WaitingSince ??= now;
            }
        });
        _reporter.Report(jobId, JobStatus.WaitingForReboot, "WaitingForReboot", null, message, items, rebootRequired: true);
        _logger.LogWarning("Job {JobId} is waiting for a restart: {Message} The agent never restarts the computer itself.", jobId, message);
        return JobOutcome.WaitingForReboot;
    }

    private static Plan BuildPlan(WindowsUpdateScanResult scan, InstallUpdatesPayload payload, Dictionary<Guid, JobItemReport> previous)
    {
        var items = new List<JobItemReport>();
        var toInstall = new List<UpdateCandidate>();
        var seen = new HashSet<Guid>();

        foreach (var candidate in scan.Missing)
        {
            var update = candidate.Update;
            if (!seen.Add(update.UpdateGuid))
            {
                continue;
            }

            var decision = InstallFilter.Evaluate(update, payload);
            previous.TryGetValue(update.UpdateGuid, out var earlier);
            if (decision.Install)
            {
                toInstall.Add(candidate);
                items.Add(NewItem(update, decision.Category, JobItemStatus.Pending, null));
                continue;
            }

            if (earlier is { Status: JobItemStatus.Succeeded })
            {
                // Installed by an earlier run of this job (now pending reboot / installed): keep the real result.
                items.Add(earlier);
                continue;
            }

            var selected = payload.Mode == InstallMode.Selected && payload.SelectedUpdateGuids.Contains(update.UpdateGuid);
            if (selected || (decision.SkipReason is not null && ReportedSkipReasons.Contains(decision.SkipReason)))
            {
                items.Add(NewItem(update, decision.Category, JobItemStatus.Skipped, decision.SkipReason));
            }
        }

        if (payload.Mode == InstallMode.Selected)
        {
            foreach (var guid in payload.SelectedUpdateGuids.Distinct())
            {
                if (!seen.Add(guid))
                {
                    continue;
                }

                previous.TryGetValue(guid, out var earlier);
                if (earlier is { Status: JobItemStatus.Succeeded })
                {
                    items.Add(earlier);
                    continue;
                }

                var installed = scan.Installed.FirstOrDefault(u => u.UpdateGuid == guid);
                items.Add(installed is not null
                    ? NewItem(installed, UpdateClassifier.Classify(installed), JobItemStatus.Skipped, SkipReasons.AlreadyInstalled)
                    : new JobItemReport
                    {
                        UpdateGuid = guid,
                        Title = earlier?.Title ?? $"Update {guid}",
                        KbNumber = earlier?.KbNumber,
                        Category = earlier?.Category ?? UpdateCategory.Other,
                        Status = JobItemStatus.Skipped,
                        SkipReason = SkipReasons.NotApplicable,
                    });
            }
        }

        // Successful results from an earlier run whose updates no longer appear at all (installed and superseded).
        foreach (var earlier in previous.Values.Where(p => p.Status == JobItemStatus.Succeeded && !seen.Contains(p.UpdateGuid)))
        {
            items.Add(earlier);
        }

        toInstall.Sort((a, b) => Priority(UpdateClassifier.Classify(a.Update)).CompareTo(Priority(UpdateClassifier.Classify(b.Update))));
        var order = toInstall.Select((c, i) => (c.Update.UpdateGuid, i)).ToDictionary(x => x.UpdateGuid, x => x.i);
        items.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
        return new Plan(toInstall, items);

        int Rank(JobItemReport item) => order.TryGetValue(item.UpdateGuid, out var position) ? position : int.MaxValue;
    }

    /// <summary>Servicing stack first (Microsoft requirement), then quality/security, definitions, drivers, feature updates last.</summary>
    internal static int Priority(UpdateCategory category) => category switch
    {
        UpdateCategory.ServicingStack => 0,
        UpdateCategory.Cumulative => 1,
        UpdateCategory.Security => 2,
        UpdateCategory.Critical => 3,
        UpdateCategory.DotNet => 4,
        UpdateCategory.Other => 5,
        UpdateCategory.Definition => 6,
        UpdateCategory.Driver => 7,
        UpdateCategory.Feature => 8,
        _ => 9,
    };

    private static JobItemReport NewItem(ReportedUpdate update, UpdateCategory category, JobItemStatus status, string? skipReason) => new()
    {
        UpdateGuid = update.UpdateGuid,
        RevisionNumber = update.RevisionNumber,
        Title = update.Title,
        KbNumber = update.KbNumbers.Select(KbNumber.Normalize).FirstOrDefault(k => k is not null),
        Category = category,
        Status = status,
        SkipReason = skipReason,
    };

    private static string Label(ReportedUpdate update) =>
        update.KbNumbers.Select(KbNumber.Normalize).FirstOrDefault(k => k is not null) ?? update.Title;

    private static void Set(List<JobItemReport> items, Guid guid, Func<JobItemReport, JobItemReport> change)
    {
        var index = items.FindIndex(i => i.UpdateGuid == guid);
        if (index >= 0)
        {
            items[index] = change(items[index]);
        }
    }

    private void Fail(List<JobItemReport> items, Guid guid, UpdateOperationResult result, string what)
    {
        var hresult = result.HResult != 0 ? result.HResult : unchecked((int)0x80240FFF);
        var code = WindowsUpdateErrors.Format(hresult);
        Set(items, guid, i => i with
        {
            Status = JobItemStatus.Failed,
            ResultCode = result.ResultCode,
            ErrorCode = code,
            ErrorMessage = $"{what}: {WindowsUpdateErrors.Describe(hresult)}",
            CompletedAt = _time.GetUtcNow(),
        });
        _logger.LogWarning("Update {UpdateGuid}: {What} with {Code} — {Description}", guid, what, code, WindowsUpdateErrors.Describe(hresult));
    }

    private static void MarkRemaining(List<JobItemReport> items, List<UpdateCandidate> toInstall, int fromIndex, JobItemStatus status, string reason)
    {
        for (var i = fromIndex; i < toInstall.Count; i++)
        {
            Set(items, toInstall[i].Update.UpdateGuid, item => item with { Status = status, SkipReason = reason });
        }
    }

    /// <summary>Converts an exception from a WUA call into a failed result (an exception never means success).</summary>
    private static async Task<UpdateOperationResult> RunOperationAsync(Func<Task<UpdateOperationResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateOperationResult(UpdateOperationResult.Failed, ex.HResult, false);
        }
    }

    private bool IsCancelRequested(Guid jobId) => _state.Read(s => s.FindJob(jobId)?.CancelRequested ?? false);

    private sealed record Plan(List<UpdateCandidate> ToInstall, List<JobItemReport> Items);
}
