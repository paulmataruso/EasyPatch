namespace EasyPatch.Contracts;

/// <summary>Normalized update category. Cumulative, ServicingStack and DotNet are derived from titles/products;
/// the others map to Microsoft update classifications.</summary>
public enum UpdateCategory
{
    Security,
    Critical,
    Cumulative,
    Definition,
    ServicingStack,
    DotNet,
    Feature,
    Driver,
    Other,
}

public enum JobType
{
    UpdateScan,
    InstallUpdates,
    InventoryRefresh,
}

public enum JobStatus
{
    Queued,
    Delivered,
    Acknowledged,
    Running,
    Completed,
    Failed,
    Cancelled,
    Expired,
    WaitingForReboot,
}

public enum JobTrigger
{
    /// <summary>Requested by an administrator.</summary>
    Manual,
    /// <summary>Created by a policy maintenance window.</summary>
    Scheduled,
    /// <summary>Created by the platform itself (e.g. retry).</summary>
    System,
}

public enum JobItemStatus
{
    Pending,
    Downloading,
    Downloaded,
    Installing,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public enum InstallMode
{
    /// <summary>Install every update the policy filter allows.</summary>
    AllEligible,
    /// <summary>Install only the explicitly selected update GUIDs.</summary>
    Selected,
}

public enum ScanTrigger
{
    Scheduled,
    Startup,
    Job,
    PostInstall,
    PostReboot,
}

public enum UpdateSourceType
{
    Unknown,
    WindowsUpdate,
    MicrosoftUpdate,
    Wsus,
}

public enum EnrollmentState
{
    Active,
    PendingApproval,
    Rejected,
    Expired,
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Expired;

    /// <summary>Statuses in which the job is held by (or on its way to) the agent.</summary>
    public static bool IsInFlight(this JobStatus status) =>
        status is JobStatus.Delivered or JobStatus.Acknowledged or JobStatus.Running or JobStatus.WaitingForReboot;
}
