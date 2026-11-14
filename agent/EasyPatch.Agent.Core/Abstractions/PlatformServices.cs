using EasyPatch.Contracts.Agent;

namespace EasyPatch.Agent.Core.Abstractions;

// Platform abstractions. Production implementations live in EasyPatch.WindowsUpdate, EasyPatch.Inventory and
// EasyPatch.Agent (Windows only). Test doubles live exclusively in test projects.

/// <summary>Opaque reference to a WUA update object, valid until disposed.</summary>
public interface IUpdateHandle : IDisposable
{
}

/// <summary>A missing update returned by a scan, plus the handle needed to download/install it.</summary>
public sealed class UpdateCandidate
{
    public UpdateCandidate(ReportedUpdate update, IUpdateHandle handle)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(handle);
        Update = update;
        Handle = handle;
    }

    public ReportedUpdate Update { get; }

    public IUpdateHandle Handle { get; }
}

/// <summary>Result of a Windows Update search. Disposing releases the underlying COM objects.</summary>
public sealed class WindowsUpdateScanResult : IDisposable
{
    public WindowsUpdateScanResult(
        IReadOnlyList<UpdateCandidate> missing,
        IReadOnlyList<ReportedUpdate> installed,
        bool microsoftUpdateRegistered)
    {
        Missing = missing ?? throw new ArgumentNullException(nameof(missing));
        Installed = installed ?? throw new ArgumentNullException(nameof(installed));
        MicrosoftUpdateRegistered = microsoftUpdateRegistered;
    }

    /// <summary>"IsInstalled=0 and IsHidden=0" (includes installed-pending-reboot updates flagged RebootRequired).</summary>
    public IReadOnlyList<UpdateCandidate> Missing { get; }

    /// <summary>"IsInstalled=1" (empty when the caller did not request installed updates).</summary>
    public IReadOnlyList<ReportedUpdate> Installed { get; }

    public bool MicrosoftUpdateRegistered { get; }

    public void Dispose()
    {
        foreach (var candidate in Missing)
        {
            candidate.Handle.Dispose();
        }
    }
}

/// <summary>Outcome of a single WUA download or install.</summary>
/// <param name="ResultCode">WUA OperationResultCode (2 Succeeded, 3 SucceededWithErrors, 4 Failed, 5 Aborted).</param>
/// <param name="HResult">HRESULT reported by WUA (0 on success).</param>
/// <param name="RebootRequired">True when the update needs a restart to complete.</param>
public sealed record UpdateOperationResult(int ResultCode, int HResult, bool RebootRequired)
{
    public const int NotStarted = 0;
    public const int InProgress = 1;
    public const int Succeeded = 2;
    public const int SucceededWithErrors = 3;
    public const int Failed = 4;
    public const int Aborted = 5;

    public bool IsSuccess => ResultCode is Succeeded or SucceededWithErrors;
}

/// <summary>Failure raised by the Windows Update integration, carrying the WUA/COM HRESULT.</summary>
public sealed class WindowsUpdateException : Exception
{
    public WindowsUpdateException()
    {
    }

    public WindowsUpdateException(string message)
        : base(message)
    {
    }

    public WindowsUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WindowsUpdateException(string message, int hresult, Exception? innerException = null)
        : base(message, innerException)
    {
        HResult = hresult;
    }
}

/// <summary>Native Windows Update operations. Implementations must never report success they did not observe.</summary>
public interface IWindowsUpdateService
{
    /// <summary>Searches for applicable, non-hidden, non-installed updates (and installed updates when requested).</summary>
    Task<WindowsUpdateScanResult> ScanAsync(bool includeInstalled, CancellationToken cancellationToken);

    /// <summary>True when Windows refuses further installs until the computer restarts.</summary>
    Task<bool> IsRebootRequiredBeforeInstallationAsync(CancellationToken cancellationToken);

    Task<UpdateOperationResult> DownloadAsync(IUpdateHandle update, CancellationToken cancellationToken);

    /// <param name="acceptEula">Accept the update's license agreement if it has not been accepted yet.</param>
    Task<UpdateOperationResult> InstallAsync(IUpdateHandle update, bool acceptEula, CancellationToken cancellationToken);

    /// <summary>Windows Update history newer than <paramref name="since"/> (newest first), at most <paramref name="maxEntries"/>.</summary>
    Task<IReadOnlyList<HistoryEntryReport>> GetHistoryAsync(DateTimeOffset? since, int maxEntries, CancellationToken cancellationToken);
}

/// <summary>Context passed to the inventory collector.</summary>
public sealed record InventoryContext(string AgentVersion, DateTimeOffset? AgentInstalledAt);

public interface IInventoryCollector
{
    Task<InventoryReport> CollectAsync(InventoryContext context, CancellationToken cancellationToken);
}

public sealed record RebootStatus(bool Pending, IReadOnlyList<string> Reasons)
{
    public static RebootStatus None { get; } = new(false, []);
}

public interface IRebootDetector
{
    Task<RebootStatus> DetectAsync(CancellationToken cancellationToken);
}

public interface IUpdateSourceDetector
{
    /// <summary>Reports the configured update source and policies. Never modifies configuration.</summary>
    UpdateSourceInfo Detect();
}

/// <summary>Protects secrets at rest (DPAPI LocalMachine on Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Installer-provided bootstrap values (HKLM\SOFTWARE\EasyPatch\Agent on Windows).</summary>
public sealed record BootstrapConfig(string? ServerUrl, string? EnrollmentToken, string? DeploymentCode, string? ProxyUrl)
{
    public static BootstrapConfig Empty { get; } = new(null, null, null, null);

    public bool HasEnrollmentCredential =>
        !string.IsNullOrWhiteSpace(EnrollmentToken) || !string.IsNullOrWhiteSpace(DeploymentCode);
}

public interface IBootstrapConfigSource
{
    BootstrapConfig Read();

    /// <summary>Removes the enrollment token and deployment code once they have been used.</summary>
    void ClearEnrollmentSecrets();
}

public interface IBootTimeProvider
{
    DateTimeOffset GetLastBootTime();
}

/// <summary>Boot time derived from the monotonic tick counter (cross-platform, no WMI required).</summary>
public sealed class TickCountBootTimeProvider : IBootTimeProvider
{
    private readonly TimeProvider _timeProvider;

    public TickCountBootTimeProvider(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset GetLastBootTime()
    {
        var boot = _timeProvider.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
        // Round to whole seconds so repeated calls produce a stable value.
        return new DateTimeOffset(boot.Ticks - (boot.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}

/// <summary>Creates the HTTP client used to talk to the server (TLS validation, proxy and timeouts are its concern).</summary>
public interface IAgentHttpClientFactory
{
    HttpClient Create(Uri serverUrl);
}

/// <summary>Always returns the same pre-configured client (tests and integration scenarios).</summary>
public sealed class FixedHttpClientFactory : IAgentHttpClientFactory
{
    private readonly HttpClient _client;

    public FixedHttpClientFactory(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public HttpClient Create(Uri serverUrl) => _client;
}

/// <summary>Bundle of the platform services an <see cref="Runtime.AgentRuntime"/> needs.</summary>
/// <remarks>The optional services enable the matching <see cref="AgentCapabilities"/>; without them the agent only
/// scans, installs and reports, exactly like an agent built before they existed.</remarks>
public sealed record AgentPlatform(
    IWindowsUpdateService WindowsUpdate,
    IInventoryCollector Inventory,
    IRebootDetector RebootDetector,
    IUpdateSourceDetector UpdateSource,
    ISecretProtector SecretProtector,
    IBootstrapConfigSource Bootstrap,
    IBootTimeProvider BootTime)
{
    /// <summary>The EasyPatch tray in each signed-in session (messages and restart prompts).</summary>
    public IUserInteraction? UserInteraction { get; init; }

    /// <summary>Restarts for administrator restart jobs.</summary>
    public ISystemRestart? SystemRestart { get; init; }

    /// <summary>Windows Update policy overrides.</summary>
    public IPolicyRegistry? PolicyRegistry { get; init; }

    /// <summary>Installs agent updates (the self-update capability also needs a release key; see
    /// <see cref="AgentRuntimeOptions.ReleasePublicKeys"/>).</summary>
    public IAgentInstaller? AgentInstaller { get; init; }

    public IUserInteraction Users => UserInteraction ?? NoUserInteraction.Instance;

    public IReadOnlyList<string> Capabilities
    {
        get
        {
            var list = new List<string>();
            if (SystemRestart is not null)
            {
                list.Add(AgentCapabilities.Restart);
            }

            if (UserInteraction is not null)
            {
                list.Add(AgentCapabilities.NotifyUser);
                list.Add(AgentCapabilities.Tray);
            }

            if (PolicyRegistry is not null)
            {
                list.Add(AgentCapabilities.WindowsUpdatePolicy);
            }

            return list;
        }
    }
}
