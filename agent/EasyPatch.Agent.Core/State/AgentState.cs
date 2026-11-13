using EasyPatch.Contracts.Agent;

namespace EasyPatch.Agent.Core.State;

public enum AgentEnrollmentStatus
{
    NotEnrolled,
    PendingApproval,
    Active,
    Rejected,

    /// <summary>The server revoked or no longer recognizes this agent's credential.</summary>
    Revoked,
}

public enum JobRunState
{
    /// <summary>Persisted locally, acknowledgement not yet confirmed by the server.</summary>
    Received,
    Acknowledged,
    Running,

    /// <summary>Parked until the computer restarts (Windows requires a reboot before installing).</summary>
    WaitingForReboot,
}

/// <summary>Durable agent state, persisted as JSON under &lt;data&gt;/state/agent-state.json.</summary>
public sealed class AgentState
{
    public int SchemaVersion { get; set; } = 1;

    public string? ServerUrl { get; set; }

    public AgentEnrollmentStatus Status { get; set; } = AgentEnrollmentStatus.NotEnrolled;

    public Guid? EndpointId { get; set; }

    public Guid? OrganizationId { get; set; }

    /// <summary>Active credential (c_…).</summary>
    public string? CredentialId { get; set; }

    public DateTimeOffset? CredentialExpiresAt { get; set; }

    /// <summary>Pending enrollment request (r_…) while waiting for administrator approval.</summary>
    public string? PendingEnrollmentRequestId { get; set; }

    /// <summary>Fingerprint of the key the current credential belongs to.</summary>
    public string? KeyFingerprint { get; set; }

    public DateTimeOffset? EnrolledAt { get; set; }

    public DateTimeOffset? FirstStartedAt { get; set; }

    public DateTimeOffset? LastCheckInAt { get; set; }

    /// <summary>Last successful scan.</summary>
    public DateTimeOffset? LastScanAt { get; set; }

    public DateTimeOffset? LastScanAttemptAt { get; set; }

    public DateTimeOffset? LastInventoryAt { get; set; }

    public DateTimeOffset? LastHistorySyncAt { get; set; }

    /// <summary>Newest Windows Update history entry already sent to the server.</summary>
    public DateTimeOffset? LastHistoryEntryDate { get; set; }

    /// <summary>Boot time observed on the previous run; a change triggers a post-reboot scan.</summary>
    public DateTimeOffset? LastBootAt { get; set; }

    public AgentConfig Config { get; set; } = new();

    public double ServerClockOffsetSeconds { get; set; }

    public string? LastAuthFailureCode { get; set; }

    public DateTimeOffset? LastAuthFailureAt { get; set; }

    /// <summary>Jobs held by the agent, in delivery order. At most one is executing at a time.</summary>
    public List<JobRecord> Jobs { get; set; } = [];

    public JobRecord? FindJob(Guid jobId) => Jobs.Find(j => j.Job.JobId == jobId);
}

/// <summary>A job the agent has accepted, with everything needed to resume after a crash or restart.</summary>
public sealed class JobRecord
{
    public required AgentJob Job { get; set; }

    public JobRunState RunState { get; set; } = JobRunState.Received;

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public string? Phase { get; set; }

    /// <summary>Next status-report sequence number (monotonic, persisted before each report).</summary>
    public long NextSequence { get; set; } = 1;

    /// <summary>Number of times execution has started; protects against crash loops.</summary>
    public int Attempts { get; set; }

    public bool CancelRequested { get; set; }

    /// <summary>Boot time when the job entered <see cref="JobRunState.WaitingForReboot"/>.</summary>
    public DateTimeOffset? WaitingBootTime { get; set; }

    public DateTimeOffset? WaitingSince { get; set; }

    /// <summary>Latest known per-update progress (used to keep earlier results across a resume).</summary>
    public List<JobItemReport> Items { get; set; } = [];
}
