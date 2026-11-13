namespace EasyPatch.Core.Entities;

public enum UserRole
{
    SuperAdmin,
    Admin,
    ReadOnly,
}

public enum EndpointStatus
{
    Active,
    Revoked,
    Decommissioned,
}

/// <summary>Per-endpoint update state. Only scans move an update to <see cref="Installed"/>.</summary>
public enum EndpointUpdateState
{
    Missing,
    Failed,
    PendingVerification,
    PendingReboot,
    Installed,
    Superseded,
    NotApplicable,
    Excluded,
}

public enum ComplianceStatus
{
    NeverScanned,
    ScanStale,
    FailedUpdates,
    MissingUpdates,
    RebootRequired,
    Compliant,
}

public enum OsSupportStatus
{
    Unknown,
    Supported,
    ExtendedSecurityUpdates,
    Unsupported,
}

public enum EsuOverride
{
    Auto,
    Enrolled,
    NotEnrolled,
}

public enum PolicyScope
{
    Global,
    Organization,
    Site,
}

public enum EnrollmentRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

public enum ScanStatus
{
    Succeeded,
    Failed,
}

public enum EventSeverity
{
    Info,
    Warning,
    Error,
}
