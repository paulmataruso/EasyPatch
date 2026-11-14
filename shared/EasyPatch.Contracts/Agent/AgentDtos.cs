using EasyPatch.Contracts.Policy;

namespace EasyPatch.Contracts.Agent;

// ---------------------------------------------------------------------------------------------
// Enrollment
// ---------------------------------------------------------------------------------------------

/// <summary>First request from a new agent. Exactly one of <see cref="EnrollmentToken"/> or
/// <see cref="DeploymentCode"/> must be supplied.</summary>
public sealed record EnrollRequest
{
    /// <summary>Secret, use-limited token (immediate activation).</summary>
    public string? EnrollmentToken { get; init; }

    /// <summary>Non-secret deployment code (creates a pending request that an admin must approve).</summary>
    public string? DeploymentCode { get; init; }

    /// <summary>Base64 SubjectPublicKeyInfo of the agent's ECDSA P-256 key. The private key never leaves the device.</summary>
    public required string PublicKey { get; init; }

    public required string AgentVersion { get; init; }

    public required DeviceIdentity Device { get; init; }
}

public sealed record DeviceIdentity
{
    public required string Hostname { get; init; }
    public string? DnsDomain { get; init; }
    public string? SmbiosUuid { get; init; }
    public string? SerialNumber { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? OsCaption { get; init; }
    public string? OsBuild { get; init; }
    public string? Architecture { get; init; }
}

/// <summary>Returned by enroll and by the pending-enrollment status poll.</summary>
public sealed record EnrollResponse
{
    public required EnrollmentState State { get; init; }

    /// <summary><c>c_…</c> for an active credential, <c>r_…</c> for a pending enrollment request.</summary>
    public required string CredentialId { get; init; }

    public Guid? EndpointId { get; init; }
    public Guid? OrganizationId { get; init; }
    public DateTimeOffset? CredentialExpiresAt { get; init; }
    public AgentConfig? Config { get; init; }
    public required string KeyFingerprint { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset ServerTime { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Check-in (heartbeat + job poll)
// ---------------------------------------------------------------------------------------------

public sealed record AgentConfig
{
    public int CheckInIntervalSeconds { get; init; } = 60;
    public int ActiveJobCheckInIntervalSeconds { get; init; } = 15;
    public int ScanIntervalMinutes { get; init; } = 360;
    public int InventoryIntervalMinutes { get; init; } = 1440;
    public int JitterPercent { get; init; } = 10;

    public UserExperienceConfig UserExperience { get; init; } = new();

    /// <summary>Windows Update settings to enforce. Null (or a null group) means leave Windows' configuration alone.</summary>
    public WindowsUpdatePolicySettings? WindowsUpdate { get; init; }
}

public sealed record CheckInRequest
{
    public required string AgentVersion { get; init; }
    public DateTimeOffset? LastBootAt { get; init; }
    public bool RebootPending { get; init; }
    public IReadOnlyList<string> RebootReasons { get; init; } = [];

    /// <summary>Jobs the agent currently holds in its durable state. Server re-delivers any in-flight job not listed.</summary>
    public IReadOnlyList<Guid> ActiveJobIds { get; init; } = [];

    /// <summary>Short human-readable activity, e.g. "Idle", "Scanning", "Installing KB5044284".</summary>
    public string? CurrentActivity { get; init; }

    /// <summary><see cref="AgentCapabilities"/> this agent supports. Agents that send none get only the original job types.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public UserSessionInfo? Sessions { get; init; }

    /// <summary>Windows Update policy enforcement status; sent only when it has changed.</summary>
    public WindowsUpdatePolicyReport? WindowsUpdatePolicy { get; init; }

    /// <summary>Package architecture of this agent build (<c>x64</c> or <c>arm64</c>): agent updates use the same one.</summary>
    public string? AgentArchitecture { get; init; }

    /// <summary>IDs of the release keys this agent accepts updates from (see <see cref="AgentRelease.KeyId"/>).
    /// Empty when the agent cannot update itself.</summary>
    public IReadOnlyList<string> ReleaseKeyIds { get; init; } = [];
}

public sealed record CheckInResponse
{
    public DateTimeOffset ServerTime { get; init; }
    public required AgentConfig Config { get; init; }

    /// <summary>Jobs to execute (newly leased or re-delivered). The agent processes them one at a time.</summary>
    public IReadOnlyList<AgentJob> Jobs { get; init; } = [];

    /// <summary>Jobs the agent holds that an administrator asked to cancel.</summary>
    public IReadOnlyList<Guid> CancelJobIds { get; init; } = [];

    /// <summary>The credential is close to expiry; the agent should rotate its key.</summary>
    public bool RotateCredential { get; init; }

    public AgentVersionInfo? LatestAgent { get; init; }
}

public sealed record AgentVersionInfo
{
    public required string Version { get; init; }
    public string? FileName { get; init; }
    public string? Sha256 { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------------------------

public sealed record AgentJob
{
    public required Guid JobId { get; init; }
    public required JobType Type { get; init; }
    public JobTrigger Trigger { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Present for <see cref="JobType.InstallUpdates"/>.</summary>
    public InstallUpdatesPayload? Install { get; init; }

    /// <summary>Present for <see cref="JobType.Restart"/>.</summary>
    public RestartPayload? Restart { get; init; }

    /// <summary>Present for <see cref="JobType.NotifyUser"/>.</summary>
    public NotifyUserPayload? Notify { get; init; }

    /// <summary>Present for <see cref="JobType.AgentUpdate"/>.</summary>
    public AgentUpdatePayload? AgentUpdate { get; init; }
}

public sealed record InstallUpdatesPayload
{
    public InstallMode Mode { get; init; }
    public IReadOnlyList<Guid> SelectedUpdateGuids { get; init; } = [];
    public required InstallFilterOptions Filter { get; init; }

    /// <summary>Do not start installing new updates after this time (end of a maintenance window).</summary>
    public DateTimeOffset? NotAfter { get; init; }
}

public sealed record InstallFilterOptions
{
    public IReadOnlyList<UpdateCategory> Categories { get; init; } = [];
    public bool IncludeOptional { get; init; }
    public IReadOnlyList<string> ExcludedKbs { get; init; } = [];
    public IReadOnlyList<Guid> ExcludedUpdateGuids { get; init; } = [];
    public bool AcceptEulas { get; init; } = true;
}

public sealed record JobAckResponse
{
    public bool Accepted { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Progress / result report. <see cref="Sequence"/> increases monotonically per job; the
/// server ignores reports with a sequence it has already processed.</summary>
public sealed record JobStatusReport
{
    public required long Sequence { get; init; }
    public required JobStatus Status { get; init; }
    public string? Phase { get; init; }
    public int? Percent { get; init; }
    public string? Message { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool RebootRequired { get; init; }
    public IReadOnlyList<JobItemReport> Items { get; init; } = [];
    public JobResultSummary? Result { get; init; }
}

public sealed record JobItemReport
{
    public required Guid UpdateGuid { get; init; }
    public int RevisionNumber { get; init; }
    public required string Title { get; init; }
    public string? KbNumber { get; init; }
    public UpdateCategory Category { get; init; }
    public required JobItemStatus Status { get; init; }

    /// <summary>One of <see cref="SkipReasons"/> when <see cref="Status"/> is Skipped.</summary>
    public string? SkipReason { get; init; }

    /// <summary>WUA OperationResultCode (0 NotStarted, 1 InProgress, 2 Succeeded, 3 SucceededWithErrors, 4 Failed, 5 Aborted).</summary>
    public int? ResultCode { get; init; }

    /// <summary>HRESULT formatted as 0xXXXXXXXX.</summary>
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public bool RebootRequired { get; init; }
    public int? DownloadPercent { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed record JobResultSummary
{
    public int Planned { get; init; }
    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public bool RebootRequired { get; init; }
    public Guid? PostInstallScanId { get; init; }
}

public static class SkipReasons
{
    public const string AlreadyInstalled = "AlreadyInstalled";
    public const string PendingReboot = "PendingReboot";
    public const string NotApplicable = "NotApplicable";
    public const string NotSelected = "NotSelected";
    public const string CategoryNotAllowed = "CategoryNotAllowed";
    public const string OptionalNotAllowed = "OptionalNotAllowed";
    public const string Excluded = "Excluded";
    public const string RequiresUserInput = "RequiresUserInput";
    public const string EulaNotAccepted = "EulaNotAccepted";
    public const string MaintenanceWindowClosed = "MaintenanceWindowClosed";
    public const string Cancelled = "Cancelled";

    public static string Describe(string? reason) => reason switch
    {
        AlreadyInstalled => "Already installed",
        PendingReboot => "Installed, waiting for a restart",
        NotApplicable => "No longer offered by Windows Update",
        NotSelected => "Not selected",
        CategoryNotAllowed => "Category not allowed by policy",
        OptionalNotAllowed => "Optional update not allowed by policy",
        Excluded => "Excluded",
        RequiresUserInput => "Requires interactive user input",
        EulaNotAccepted => "License agreement not accepted by policy",
        MaintenanceWindowClosed => "Maintenance window closed",
        Cancelled => "Cancelled",
        null => "",
        _ => reason,
    };
}

// ---------------------------------------------------------------------------------------------
// Scans and history
// ---------------------------------------------------------------------------------------------

public sealed record ScanResultReport
{
    /// <summary>Agent-generated ID; makes ingestion idempotent across retries.</summary>
    public required Guid ScanId { get; init; }
    public Guid? JobId { get; init; }
    public ScanTrigger Trigger { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public UpdateSourceInfo? Source { get; init; }

    /// <summary>Result of "IsInstalled=0 and IsHidden=0" (includes updates installed but awaiting reboot,
    /// flagged with <see cref="ReportedUpdate.RebootRequired"/>).</summary>
    public IReadOnlyList<ReportedUpdate> Missing { get; init; } = [];

    /// <summary>Result of "IsInstalled=1".</summary>
    public IReadOnlyList<ReportedUpdate> Installed { get; init; } = [];
}

public sealed record ReportedUpdate
{
    public required Guid UpdateGuid { get; init; }
    public int RevisionNumber { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> KbNumbers { get; init; } = [];
    public IReadOnlyList<UpdateCategoryRef> Categories { get; init; } = [];
    public bool IsDriver { get; init; }
    public bool BrowseOnly { get; init; }
    public bool IsMandatory { get; init; }
    public bool IsDownloaded { get; init; }
    public bool IsInstalled { get; init; }

    /// <summary>WUA IUpdate.RebootRequired: installed, but a restart is required to complete it.</summary>
    public bool RebootRequired { get; init; }
    public bool EulaAccepted { get; init; } = true;
    public bool CanRequestUserInput { get; init; }

    /// <summary>InstallationBehavior.RebootBehavior: 0 NeverReboots, 1 AlwaysRequiresReboot, 2 CanRequestReboot.</summary>
    public int? RebootBehavior { get; init; }
    public string? MsrcSeverity { get; init; }

    /// <summary>IUpdate.LastDeploymentChangeTime — closest available proxy for a release date.</summary>
    public DateTimeOffset? LastDeploymentChangeTime { get; init; }
    public long? MaxDownloadSize { get; init; }
    public string? SupportUrl { get; init; }
    public IReadOnlyList<Guid> SupersededUpdateGuids { get; init; } = [];
}

public sealed record UpdateCategoryRef
{
    public Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>WUA category type, e.g. "UpdateClassification", "Product", "ProductFamily", "Company".</summary>
    public string? Type { get; init; }
}

public sealed record UpdateSourceInfo
{
    public UpdateSourceType Type { get; init; }
    public string? WsusServer { get; init; }
    public bool MicrosoftUpdateRegistered { get; init; }
    public bool WufbPoliciesConfigured { get; init; }
    public int? DeferQualityUpdatesDays { get; init; }
    public int? DeferFeatureUpdatesDays { get; init; }
    public string? TargetReleaseVersion { get; init; }

    /// <summary>NotConfigured, Disabled, NotifyDownload, AutoDownloadNotifyInstall, AutoDownloadScheduleInstall, LocalAdminChooses, Unknown.</summary>
    public string? AutomaticUpdatesMode { get; init; }
    public bool ManagedByMdm { get; init; }
}

public sealed record HistoryReport
{
    public IReadOnlyList<HistoryEntryReport> Entries { get; init; } = [];
}

public sealed record HistoryEntryReport
{
    public Guid? UpdateGuid { get; init; }
    public int? RevisionNumber { get; init; }
    public required string Title { get; init; }
    public string? KbNumber { get; init; }

    /// <summary>Installation, Uninstallation or Other.</summary>
    public required string Operation { get; init; }

    /// <summary>WUA OperationResultCode.</summary>
    public int ResultCode { get; init; }
    public string? HResult { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? ClientApplicationId { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Inventory
// ---------------------------------------------------------------------------------------------

public sealed record InventoryReport
{
    public required string Hostname { get; init; }
    public string? DnsDomain { get; init; }
    public bool? DomainJoined { get; init; }
    public string? OsCaption { get; init; }

    /// <summary>Registry EditionID, e.g. Professional, Enterprise, EnterpriseS, ServerStandard.</summary>
    public string? OsEdition { get; init; }

    /// <summary>Registry DisplayVersion, e.g. 24H2.</summary>
    public string? OsDisplayVersion { get; init; }
    public int? OsBuildNumber { get; init; }
    public int? OsUbr { get; init; }

    /// <summary>Workstation, DomainController or Server.</summary>
    public string? OsProductType { get; init; }
    public DateTimeOffset? OsInstallDate { get; init; }
    public string? Architecture { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? SerialNumber { get; init; }
    public string? SmbiosUuid { get; init; }
    public string? ProcessorName { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? SystemDriveTotalBytes { get; init; }
    public long? SystemDriveFreeBytes { get; init; }
    public DateTimeOffset? LastBootAt { get; init; }
    public string? TimeZoneId { get; init; }
    public bool? IsVirtualMachine { get; init; }
    public bool? EsuLicenseDetected { get; init; }
    public IReadOnlyList<string> EsuLicenseNames { get; init; } = [];
    public string? WindowsUpdateServiceStartMode { get; init; }
    public string? WindowsUpdateServiceState { get; init; }
    public required string AgentVersion { get; init; }
    public DateTimeOffset? AgentInstalledAt { get; init; }
    public bool RebootPending { get; init; }
    public IReadOnlyList<string> RebootReasons { get; init; } = [];
    public UpdateSourceInfo? UpdateSource { get; init; }
}

// ---------------------------------------------------------------------------------------------
// Credentials
// ---------------------------------------------------------------------------------------------

public sealed record RotateCredentialRequest
{
    public required string PublicKey { get; init; }
}

public sealed record RotateCredentialResponse
{
    public required string CredentialId { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public required string KeyFingerprint { get; init; }
}

public sealed record UnenrollRequest
{
    public string? Reason { get; init; }
}

/// <summary>Machine-readable error codes in ProblemDetails "code" for agent routes.</summary>
public static class AgentErrorCodes
{
    public const string InvalidSignature = "invalid_signature";
    public const string ClockSkew = "clock_skew";
    public const string ReplayedNonce = "replayed_nonce";
    public const string UnknownCredential = "unknown_credential";
    public const string CredentialRevoked = "credential_revoked";
    public const string CredentialExpired = "credential_expired";
    public const string EndpointRevoked = "endpoint_revoked";
    public const string EnrollmentInvalid = "enrollment_invalid";
    public const string EnrollmentRejected = "enrollment_rejected";
    public const string JobNotFound = "job_not_found";
    public const string JobCancelled = "job_cancelled";
    public const string RateLimited = "rate_limited";
    public const string ValidationFailed = "validation_failed";
}

public static class AgentRoutes
{
    public const string Prefix = "api/v1/agents";
    public const string Enroll = Prefix + "/enroll";
    public const string EnrollmentStatus = Prefix + "/enrollment/status";
    public const string CheckIn = Prefix + "/check-in";
    public const string Inventory = Prefix + "/inventory";
    public const string ScanResults = Prefix + "/updates/scan-results";
    public const string History = Prefix + "/updates/history";
    public const string RotateCredential = Prefix + "/credentials/rotate";
    public const string Unenroll = Prefix + "/unenroll";

    public static string JobAck(Guid jobId) => $"{Prefix}/jobs/{jobId:D}/ack";
    public static string JobStatus(Guid jobId) => $"{Prefix}/jobs/{jobId:D}/status";
}
