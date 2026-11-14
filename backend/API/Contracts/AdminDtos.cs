using EasyPatch.Contracts;
using EasyPatch.Contracts.Policy;
using EasyPatch.Core.Domain;
using EasyPatch.Core.Entities;

namespace EasyPatch.Api.Contracts;

// Administrator API data transfer objects. JSON is camelCase with string enums (see ContractJson).

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

// ---------------------------------------------------------------------------------------------
// Authentication and account
// ---------------------------------------------------------------------------------------------

public sealed record LoginRequest(string Username, string Password);

public sealed record LoginResponse(bool MfaRequired, bool MfaSetupRequired, CurrentUserDto User);

public sealed record MfaCodeRequest(string Code);

public sealed record CurrentUserDto(
    Guid Id,
    string Username,
    string Email,
    UserRole Role,
    bool MfaEnabled,
    bool MfaCompleted,
    bool MfaSetupRequired,
    bool MustChangePassword,
    IReadOnlyList<Guid> OrganizationIds,
    DateTimeOffset? LastLoginAt);

public sealed record MfaSetupResponse(string Secret, string OtpAuthUri, string QrCodeDataUri);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record MfaDisableRequest(string Password, string Code);

public sealed record PasswordConfirmRequest(string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record SessionDto(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, string? IpAddress, string? UserAgent,
    bool Current);

// ---------------------------------------------------------------------------------------------
// Users (SuperAdmin)
// ---------------------------------------------------------------------------------------------

public sealed record UserDto(
    Guid Id,
    string Username,
    string Email,
    UserRole Role,
    bool IsActive,
    bool MfaEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LockoutUntil,
    IReadOnlyList<Guid> OrganizationIds);

public sealed record CreateUserRequest(string Username, string Email, UserRole Role, string? Password, IReadOnlyList<Guid>? OrganizationIds);

/// <summary>When no password is supplied, <see cref="ResetLink"/> carries a one-time link the new user opens to set one.</summary>
public sealed record CreateUserResponse(UserDto User, ResetLinkResponse? ResetLink);

public sealed record UpdateUserRequest(string? Email, UserRole? Role, bool? IsActive);

public sealed record ResetLinkResponse(string Url, DateTimeOffset ExpiresAt);

public sealed record OrganizationAccessRequest(IReadOnlyList<Guid> OrganizationIds);

// ---------------------------------------------------------------------------------------------
// Organizations, sites, policies
// ---------------------------------------------------------------------------------------------

public sealed record OrganizationSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    string TimeZone,
    bool IsActive,
    int SiteCount,
    int EndpointCount,
    int OnlineCount,
    int OfflineCount,
    int CompliantCount,
    int NonCompliantCount,
    int MissingRequiredUpdates,
    int FailedCount,
    int RebootRequiredCount,
    double CompliancePercent,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset CreatedAt);

public sealed record OrganizationDto(
    Guid Id,
    string Name,
    string? Description,
    string? ContactName,
    string? ContactEmail,
    string? Notes,
    string TimeZone,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<SiteDto> Sites,
    OrganizationSummaryDto Summary);

public sealed record OrganizationRequest(string Name, string? Description, string? ContactName, string? ContactEmail, string? Notes, string? TimeZone);

public sealed record SiteDto(
    Guid Id,
    Guid OrganizationId,
    string OrganizationName,
    string Name,
    string? Description,
    string? Address,
    string TimeZone,
    bool IsActive,
    int EndpointCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record SiteRequest(string Name, string? Description, string? Address, string? TimeZone);

/// <summary>A policy layer. Null values inherit from the parent scope. <c>MaintenanceStart</c> is "HH:mm" in the site
/// (or organization) time zone; days use 0 = Sunday … 6 = Saturday.</summary>
public sealed record PolicyDto(
    Guid? Id,
    PolicyScope Scope,
    Guid? OrganizationId,
    Guid? SiteId,
    int? CheckInIntervalSeconds,
    int? ScanIntervalMinutes,
    int? InventoryIntervalMinutes,
    int? JitterPercent,
    IReadOnlyList<string>? AllowedCategories,
    bool? IncludeOptional,
    IReadOnlyList<string> ExcludedKbs,
    bool? AcceptEulas,
    bool? AutoInstallEnabled,
    IReadOnlyList<int>? MaintenanceDays,
    string? MaintenanceStart,
    int? MaintenanceDurationMinutes,
    int? ScanStaleAfterHours,
    int? MaxConcurrentInstallsPerSite,
    DateTimeOffset? UpdatedAt,
    Guid? EndpointId = null,
    bool? TrayIconEnabled = null,
    bool? InstallNotificationsEnabled = null,
    RestartScheduleDto? RestartSchedule = null,
    PolicyWindowsUpdate? WindowsUpdate = null,
    bool? AgentAutoUpdateEnabled = null,
    int? InstallDelayDays = null,
    int? SecurityInstallDelayDays = null,
    Guid? TagId = null,
    bool? KnownExploitedSkipsDelay = null);

/// <summary>Recurring restart. <c>Time</c> is "HH:mm" in the site (or organization) time zone; days use 0 = Sunday … 6 = Saturday.</summary>
public sealed record RestartScheduleDto(
    bool Enabled,
    IReadOnlyList<int> Days,
    string Time,
    RestartCondition Condition,
    RestartMode Mode,
    int CountdownMinutes,
    int DeadlineMinutes,
    int ExpiryMinutes);

/// <summary>A Windows Update policy registry value EasyPatch sets (or deletes) under HKLM.</summary>
public sealed record PolicyRegistryValueDto(WindowsUpdatePolicyGroup Group, string Path, string Name, string Value);

public sealed record PolicyRequest(
    int? CheckInIntervalSeconds,
    int? ScanIntervalMinutes,
    int? InventoryIntervalMinutes,
    int? JitterPercent,
    IReadOnlyList<string>? AllowedCategories,
    bool? IncludeOptional,
    IReadOnlyList<string>? ExcludedKbs,
    bool? AcceptEulas,
    bool? AutoInstallEnabled,
    IReadOnlyList<int>? MaintenanceDays,
    string? MaintenanceStart,
    int? MaintenanceDurationMinutes,
    int? ScanStaleAfterHours,
    int? MaxConcurrentInstallsPerSite,
    bool? TrayIconEnabled = null,
    bool? InstallNotificationsEnabled = null,
    RestartScheduleDto? RestartSchedule = null,
    PolicyWindowsUpdate? WindowsUpdate = null,
    bool? AgentAutoUpdateEnabled = null,
    int? InstallDelayDays = null,
    int? SecurityInstallDelayDays = null,
    bool? KnownExploitedSkipsDelay = null);

public sealed record EffectivePolicyDto(
    int CheckInIntervalSeconds,
    int ScanIntervalMinutes,
    int InventoryIntervalMinutes,
    int JitterPercent,
    IReadOnlyList<UpdateCategory> AllowedCategories,
    bool IncludeOptional,
    IReadOnlyList<string> ExcludedKbs,
    bool AcceptEulas,
    bool AutoInstallEnabled,
    IReadOnlyList<int>? MaintenanceDays,
    string? MaintenanceStart,
    int? MaintenanceDurationMinutes,
    string TimeZone,
    int ScanStaleAfterHours,
    int MaxConcurrentInstallsPerSite,
    IReadOnlyDictionary<string, PolicyScope> Sources,
    bool TrayIconEnabled = true,
    bool InstallNotificationsEnabled = true,
    RestartScheduleDto? RestartSchedule = null,
    WindowsUpdatePolicySettings? WindowsUpdate = null,
    IReadOnlyList<PolicyRegistryValueDto>? WindowsUpdateValues = null,
    bool AgentAutoUpdateEnabled = true,
    int InstallDelayDays = 0,
    int SecurityInstallDelayDays = 0,
    IReadOnlyDictionary<string, string>? SourceTags = null,
    bool KnownExploitedSkipsDelay = true);

// ---------------------------------------------------------------------------------------------
// Tags
// ---------------------------------------------------------------------------------------------

public sealed record TagRefDto(Guid Id, string Name, string Color);

public sealed record TagDto(Guid Id, string Name, string Color, string? Description, int Priority, int EndpointCount, bool HasPolicy,
    DateTimeOffset CreatedAt);

public sealed record TagRequest(string Name, string Color, string? Description, int Priority);

public sealed record EndpointTagsRequest(IReadOnlyList<Guid> TagIds);

public sealed record BulkEndpointTagsRequest(IReadOnlyList<Guid> EndpointIds, IReadOnlyList<Guid>? AddTagIds, IReadOnlyList<Guid>? RemoveTagIds);

public sealed record BulkEndpointTagsResult(int Changed);

/// <summary>A tag policy that applies to an endpoint, in resolution order (later entries override earlier ones).</summary>
public sealed record TagPolicyLayerDto(TagRefDto Tag, int Priority, PolicyDto Policy);

// ---------------------------------------------------------------------------------------------
// Endpoints
// ---------------------------------------------------------------------------------------------

public sealed record EndpointSummaryDto(
    Guid Id,
    string Hostname,
    Guid OrganizationId,
    string OrganizationName,
    Guid? SiteId,
    string? SiteName,
    EndpointStatus Status,
    string? OperatingSystem,
    string? OsVersion,
    string? OsBuild,
    string? Architecture,
    string? AgentVersion,
    bool AgentOutdated,
    bool IsOnline,
    DateTimeOffset? LastCheckInAt,
    DateTimeOffset? LastScanAt,
    ComplianceStatus ComplianceStatus,
    int MissingRequiredCount,
    int MissingTotalCount,
    int FailedCount,
    bool RebootPending,
    OsSupportStatus OsSupportStatus,
    string? CurrentActivity,
    string? LastSeenIp,
    string Platform = "Windows",
    int MissingDeferredCount = 0,
    IReadOnlyList<TagRefDto>? Tags = null,
    int KnownExploitedMissingCount = 0);

public sealed record CredentialDto(string CredentialId, string KeyFingerprint, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    string? RevokedReason, DateTimeOffset? LastUsedAt);

public sealed record EndpointDetailDto(
    EndpointSummaryDto Summary,
    string? DnsDomain,
    bool? DomainJoined,
    string? OsEdition,
    string? OsProductType,
    DateTimeOffset? OsInstallDate,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string? ProcessorName,
    long? TotalMemoryBytes,
    long? SystemDriveTotalBytes,
    long? SystemDriveFreeBytes,
    bool? IsVirtualMachine,
    string? TimeZoneId,
    DateTimeOffset? LastBootAt,
    DateTimeOffset? AgentInstalledAt,
    DateTimeOffset EnrolledAt,
    DateTimeOffset? LastInventoryAt,
    DateTimeOffset? LastScanAttemptAt,
    string? LastScanError,
    IReadOnlyList<string> RebootReasons,
    DateTimeOffset? RebootPendingSince,
    string? UpdateSourceType,
    string? UpdateSourceServer,
    bool MicrosoftUpdateRegistered,
    bool WufbPoliciesConfigured,
    int? DeferQualityUpdatesDays,
    int? DeferFeatureUpdatesDays,
    string? TargetReleaseVersion,
    string? AutomaticUpdatesMode,
    bool ManagedByMdm,
    string? WindowsUpdateServiceStartMode,
    string? WindowsUpdateServiceState,
    DateOnly? OsEndOfServicing,
    bool EsuLicenseDetected,
    EsuOverride EsuOverride,
    int InstalledCount,
    int PendingRebootUpdateCount,
    DateTimeOffset? ComplianceEvaluatedAt,
    string? Notes,
    IReadOnlyList<CredentialDto> Credentials,
    JobSummaryDto? ActiveJob,
    IReadOnlyList<string> AgentCapabilities,
    int? SignedInUsers,
    int? TrayConnectedSessions,
    WindowsUpdatePolicyReport? WindowsUpdatePolicy,
    DateTimeOffset? WindowsUpdatePolicyReportedAt,
    bool HasOwnPolicy,
    AgentUpdateInfoDto? AgentUpdate = null,
    LinuxDetailsDto? Linux = null);

/// <summary>Linux-only endpoint details (null for Windows endpoints).</summary>
/// <param name="KeyProtection">How the agent's private key is protected: systemd-creds-host or file-permissions.</param>
public sealed record LinuxDetailsDto(string? OsId, string? OsVersion, string? KernelVersion, string? PackageManager, string? KeyProtection);

/// <summary>Whether the endpoint's agent can update itself, and to what.</summary>
/// <param name="AvailableVersion">The version an agent update would install now (null when none).</param>
/// <param name="Code">Why no update is available (e.g. <c>agent_up_to_date</c>, <c>package_not_signed</c>).</param>
public sealed record AgentUpdateInfoDto(
    bool Supported,
    string? Architecture,
    IReadOnlyList<string> ReleaseKeyIds,
    bool AutomaticUpdates,
    string? AvailableVersion,
    string? Code,
    string? Message,
    string? PackageFormat = null);

public sealed record UpdateEndpointRequest(Guid? SiteId, string? Notes, EsuOverride? EsuOverride);

public sealed record RevokeEndpointRequest(string? Reason);

public sealed record JobOverlayDto(Guid JobId, JobStatus JobStatus, JobItemStatus ItemStatus, int? DownloadPercent);

public sealed record EndpointUpdateDto(
    Guid UpdateGuid,
    int RevisionNumber,
    string Title,
    string? Description,
    string? KbNumber,
    IReadOnlyList<string> KbNumbers,
    UpdateCategory Category,
    string? MsClassification,
    string? MsrcSeverity,
    DateTimeOffset? ReleaseDate,
    long? SizeBytes,
    bool RebootLikely,
    string? SupportUrl,
    EndpointUpdateState State,
    bool IsRequired,
    bool IsExcluded,
    DateTimeOffset FirstDetectedAt,
    DateTimeOffset StateChangedAt,
    DateTimeOffset? InstalledAt,
    int FailedAttempts,
    string? ErrorCode,
    string? ErrorMessage,
    JobOverlayDto? ActiveJob,
    PackageInfoDto? Package = null,
    DateTimeOffset? DeferredUntil = null,
    IReadOnlyList<string>? CveIds = null,
    bool KnownExploited = false,
    DateOnly? KevDueDate = null);

/// <summary>Linux package behind an update. <c>InstalledVersion</c> is the endpoint's version (endpoint views only).</summary>
public sealed record PackageInfoDto(string Name, string? Architecture, string? Version, string? InstalledVersion, string? Repository,
    IReadOnlyList<string> Advisories);

public sealed record UpdateHistoryDto(long Id, Guid? UpdateGuid, string Title, string? KbNumber, string Operation, int ResultCode, string Result, string? HResult,
    string? ErrorDescription, DateTimeOffset Date);

public sealed record EndpointEventDto(long Id, Guid EndpointId, string? Hostname, string Type, EventSeverity Severity, string Message, Guid? JobId,
    DateTimeOffset CreatedAt);

public sealed record InstallUpdatesRequest(InstallMode Mode, IReadOnlyList<Guid>? UpdateGuids, bool Force);

public sealed record ExclusionRequest(Guid UpdateGuid, string? Reason);

public sealed record BulkEndpointsRequest(IReadOnlyList<Guid> EndpointIds, bool Force);

/// <summary>Restart an endpoint now or at <c>ScheduledAt</c>. If it is offline, the restart is abandoned after <c>ExpiresAfterMinutes</c>.</summary>
public sealed record RestartRequest(
    RestartMode Mode,
    RestartCondition Condition,
    int? CountdownMinutes,
    int? DeadlineMinutes,
    DateTimeOffset? ScheduledAt,
    int? ExpiresAfterMinutes,
    bool Force);

/// <summary>Show a preset message. <c>RestartAt</c> fills in the <see cref="UserMessagePreset.RestartScheduled"/> text.</summary>
public sealed record NotifyRequest(UserMessagePreset Preset, DateTimeOffset? RestartAt);

public sealed record BulkRestartRequest(IReadOnlyList<Guid> EndpointIds, RestartRequest Restart);

public sealed record BulkNotifyRequest(IReadOnlyList<Guid> EndpointIds, NotifyRequest Message);

/// <param name="Version">A specific package version; the newest suitable package when omitted.</param>
public sealed record AgentUpdateRequest(string? Version);

public sealed record BulkAgentUpdateRequest(IReadOnlyList<Guid> EndpointIds, string? Version);

public sealed record UserMessagePresetDto(UserMessagePreset Preset, string Title, string Body, bool NeedsRestartTime);

public sealed record BulkItemResult(Guid EndpointId, string? Hostname, bool Success, Guid? JobId, string? ErrorCode, string? Message);

public sealed record BulkResultDto(int Succeeded, int Failed, IReadOnlyList<BulkItemResult> Results);

// ---------------------------------------------------------------------------------------------
// Fleet update management
// ---------------------------------------------------------------------------------------------

public sealed record FleetUpdateDto(
    Guid UpdateGuid,
    string Title,
    string? KbNumber,
    UpdateCategory Category,
    string? MsrcSeverity,
    DateTimeOffset? ReleaseDate,
    long? SizeBytes,
    int MissingCount,
    int FailedCount,
    int InstalledCount,
    int PendingRebootCount,
    int ExcludedCount,
    PackageInfoDto? Package = null,
    int CveCount = 0,
    bool KnownExploited = false);

public sealed record FleetUpdateDetailDto(FleetUpdateDto Summary, string? Description, string? SupportUrl, IReadOnlyList<string> KbNumbers,
    IReadOnlyList<string> Products, string? MsClassification, bool RebootLikely, IReadOnlyList<Guid> SupersededUpdateGuids,
    IReadOnlyList<VulnerabilityDto>? Vulnerabilities = null);

/// <summary>A CVE an update fixes. <c>Kev</c>: in the CISA Known Exploited Vulnerabilities catalog; <c>KevDueDate</c> is the
/// remediation date CISA sets for US federal agencies. Title, severity and CVSS come from MSRC (Windows CVEs only).</summary>
public sealed record VulnerabilityDto(string CveId, string? Title, string? Severity, decimal? CvssBase, bool MsrcExploited, bool Kev,
    DateOnly? KevDateAdded, DateOnly? KevDueDate, bool KevRansomware, string? KevName);

/// <summary>Synchronization state of a vulnerability feed (msrc, kev, debian).</summary>
public sealed record VulnerabilityFeedDto(string Name, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? LastError, string? Version,
    int ItemCount);

public sealed record FleetUpdateEndpointDto(
    Guid EndpointId,
    string Hostname,
    string OrganizationName,
    string? SiteName,
    bool IsOnline,
    EndpointUpdateState State,
    bool IsRequired,
    bool IsExcluded,
    int FailedAttempts,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StateChangedAt);

public sealed record FleetInstallRequest(Guid? OrganizationId, Guid? SiteId, IReadOnlyList<Guid>? EndpointIds);

// ---------------------------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------------------------

public sealed record JobSummaryDto(
    Guid Id,
    Guid EndpointId,
    string Hostname,
    Guid OrganizationId,
    string OrganizationName,
    JobType Type,
    JobStatus Status,
    JobTrigger Trigger,
    string RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset ExpiresAt,
    int? ProgressPercent,
    string? Phase,
    string? ProgressMessage,
    string? ErrorCode,
    string? ErrorMessage,
    bool RebootRequired,
    bool TimedOut,
    bool CancelRequested,
    int ItemCount,
    int SucceededCount,
    int FailedCount);

public sealed record JobItemDto(
    Guid UpdateGuid,
    string Title,
    string? KbNumber,
    UpdateCategory Category,
    JobItemStatus Status,
    string? SkipReason,
    int? ResultCode,
    string? ErrorCode,
    string? ErrorMessage,
    bool RebootRequired,
    int? DownloadPercent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record JobDetailDto(
    JobSummaryDto Summary,
    InstallMode? InstallMode,
    DateTimeOffset? NotBefore,
    DateTimeOffset? DeliveredAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? LastProgressAt,
    int DeliveryCount,
    Guid? RetryOfJobId,
    DateTimeOffset? CancelRequestedAt,
    IReadOnlyList<JobItemDto> Items,
    IReadOnlyList<EndpointEventDto> Events);

// ---------------------------------------------------------------------------------------------
// Agent management
// ---------------------------------------------------------------------------------------------

public sealed record EnrollmentTokenDto(
    Guid Id,
    Guid OrganizationId,
    string OrganizationName,
    Guid? SiteId,
    string? SiteName,
    string TokenPrefix,
    string? Description,
    int MaxUses,
    int UseCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    string State);

public sealed record CreateTokensRequest(Guid OrganizationId, Guid? SiteId, string? Description, int ExpiresInHours, int MaxUses, int Count);

/// <param name="LinuxEnrollCommand">Enrolls an installed Linux agent; the token is piped in so it never shows in the process list.</param>
public sealed record CreatedTokenDto(EnrollmentTokenDto Token, string PlaintextToken, string MsiExecCommand, string? LinuxEnrollCommand = null);

public sealed record DeploymentCodeDto(
    Guid Id,
    string Code,
    string Name,
    Guid OrganizationId,
    string OrganizationName,
    Guid? SiteId,
    string? SiteName,
    int MaxPendingRequests,
    IReadOnlyList<string> AllowedSourceCidrs,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    int PendingCount,
    int ApprovedCount,
    string State,
    string OneLiner,
    string ScriptUrl,
    string? LinuxOneLiner = null,
    string? LinuxScriptUrl = null);

public sealed record CreateDeploymentCodeRequest(Guid OrganizationId, Guid? SiteId, string Name, int? ExpiresInDays, IReadOnlyList<string>? AllowedSourceCidrs,
    int? MaxPendingRequests);

public sealed record InstallScriptResponse(string Script, string OneLiner, string ScriptUrl, string PackageFileName, string PackageVersion, string Sha256,
    bool RequireSignature, string Platform = "Windows");

public sealed record EnrollmentRequestDto(
    Guid Id,
    string RequestId,
    string Hostname,
    string KeyFingerprint,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string? OsCaption,
    string AgentVersion,
    string? SourceIp,
    EnrollmentRequestStatus Status,
    Guid OrganizationId,
    string OrganizationName,
    Guid? SiteId,
    string? SiteName,
    string DeploymentCodeName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    string? RejectReason,
    Guid? EndpointId,
    Guid? ExistingEndpointId);

public sealed record ApproveRequest(Guid? OrganizationId, Guid? SiteId);

public sealed record RejectRequest(string? Reason);

public sealed record BulkApproveRequest(IReadOnlyList<Guid> Ids);

/// <param name="ReleaseKeyId">The release key in the package's <c>.sig</c> file (null: not signed, so agents will not
/// update to it).</param>
public sealed record AgentPackageDto(string FileName, string Version, string Architecture, long SizeBytes, string Sha256, DateTimeOffset ModifiedAt,
    string DownloadUrl, bool IsLatest, string? ReleaseKeyId = null, string? SignatureProblem = null, string Format = "msi");

public sealed record AgentVersionCountDto(string Version, int Count, bool IsLatest);

// ---------------------------------------------------------------------------------------------
// Dashboard and reports
// ---------------------------------------------------------------------------------------------

public sealed record DashboardSummaryDto(
    int Organizations,
    int Endpoints,
    int Online,
    int Offline,
    int Compliant,
    int MissingUpdates,
    int FailedUpdates,
    int RebootRequired,
    int ScanStale,
    int NeverScanned,
    double CompliancePercent,
    int MissingRequiredUpdates,
    int QueuedJobs,
    int RunningJobs,
    int FailedJobs24h,
    int PendingApprovals,
    int UnsupportedOs,
    int OutdatedAgents,
    IReadOnlyList<OrganizationSummaryDto> LeastCompliantOrganizations,
    int KnownExploitedEndpoints = 0);

public sealed record ComplianceReportRow(
    Guid EndpointId,
    string Hostname,
    string OrganizationName,
    string? SiteName,
    string? OperatingSystem,
    string? OsBuild,
    ComplianceStatus ComplianceStatus,
    int MissingRequiredCount,
    int MissingTotalCount,
    int FailedCount,
    bool RebootPending,
    bool IsOnline,
    OsSupportStatus OsSupportStatus,
    DateTimeOffset? LastScanAt,
    DateTimeOffset? LastCheckInAt,
    string Platform = "Windows");

public sealed record ComplianceReportDto(
    int Total,
    int Compliant,
    double CompliancePercent,
    IReadOnlyDictionary<ComplianceStatus, int> ByStatus,
    IReadOnlyList<ComplianceReportRow> Rows);

public sealed record ComplianceTrendPoint(DateOnly Date, int TotalEndpoints, int Compliant, int MissingUpdates, int FailedUpdates, int RebootRequired, int ScanStale,
    int NeverScanned, int Offline, int MissingRequiredUpdates, double CompliancePercent);

public sealed record InstallationReportRow(
    Guid JobId,
    Guid EndpointId,
    string Hostname,
    string OrganizationName,
    string? SiteName,
    Guid UpdateGuid,
    string Title,
    string? KbNumber,
    UpdateCategory Category,
    JobItemStatus Status,
    string? SkipReason,
    string? ErrorCode,
    string? ErrorMessage,
    bool RebootRequired,
    string RequestedBy,
    DateTimeOffset? CompletedAt);

public sealed record AuditLogDto(long Id, Guid? UserId, string? Username, Guid? OrganizationId, Guid? EndpointId, string Action, string? TargetType, string? TargetId,
    string? Details, string? IpAddress, string? CorrelationId, DateTimeOffset CreatedAt);

public sealed record OsLifecycleDto(Guid Id, string Product, string EditionGroup, int BuildNumber, string VersionLabel, DateOnly EndOfServicing, DateOnly? EsuEndDate,
    string? Notes);

public sealed record OsLifecycleRequest(string Product, string EditionGroup, int BuildNumber, string VersionLabel, DateOnly EndOfServicing, DateOnly? EsuEndDate,
    string? Notes);

public sealed record ServerInfoDto(string Version, string PublicUrl, DateTimeOffset ServerTime);
