namespace EasyPatch.Agent.Core;

public sealed class AgentRuntimeOptions
{
    /// <summary>Root data directory (C:\ProgramData\EasyPatch on Windows).</summary>
    public required string DataDirectory { get; init; }

    public required string AgentVersion { get; init; }

    /// <summary>
    /// Reject non-HTTPS server URLs. Production always leaves this on; only in-process tests (TestServer) turn it off.
    /// This never affects TLS certificate validation, which cannot be disabled.
    /// </summary>
    public bool RequireHttps { get; init; } = true;

    public int MaxOutboxItems { get; init; } = 500;

    public long MaxOutboxBytes { get; init; } = 256L * 1024 * 1024;

    public int MaxHistoryEntriesPerSync { get; init; } = 500;

    /// <summary>A job that has been started this many times without finishing is failed (crash-loop protection).</summary>
    public int MaxJobAttempts { get; init; } = 3;

    /// <summary>Jobs parked waiting for a restart are failed after this long.</summary>
    public TimeSpan WaitingForRebootMaxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>A change in boot time larger than this means the computer restarted.</summary>
    public TimeSpan BootTimeTolerance { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan WorkLoopInterval { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan NotConfiguredRetryInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Retry interval while the credential is revoked / enrollment was rejected.</summary>
    public TimeSpan RevokedRetryInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Delay between pending-approval status polls (before jitter).</summary>
    public TimeSpan PendingApprovalPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public string LogDirectory => Path.Combine(DataDirectory, "logs");
}
