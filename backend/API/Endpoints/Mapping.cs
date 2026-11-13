using System.Linq.Expressions;
using EasyPatch.Api.Contracts;
using EasyPatch.Contracts;
using EasyPatch.Contracts.Updates;
using EasyPatch.Core.Domain;
using EasyPatch.Core.Entities;

namespace EasyPatch.Api.Endpoints;

internal static class Mapping
{
    public static readonly Expression<Func<Job, JobSummaryDto>> JobSummary = j => new JobSummaryDto(
        j.Id, j.EndpointId, j.Endpoint!.Hostname, j.OrganizationId, j.Endpoint.Organization!.Name, j.Type, j.Status, j.Trigger, j.RequestedBy,
        j.CreatedAt, j.StartedAt, j.CompletedAt, j.ExpiresAt, j.ProgressPercent, j.Phase, j.ProgressMessage, j.ErrorCode, j.ErrorMessage,
        j.RebootRequired, j.TimedOut, j.CancelRequestedAt != null,
        j.Updates.Count,
        j.Updates.Count(u => u.Status == JobItemStatus.Succeeded),
        j.Updates.Count(u => u.Status == JobItemStatus.Failed));

    public static readonly Expression<Func<EndpointEvent, EndpointEventDto>> Event = e => new EndpointEventDto(
        e.Id, e.EndpointId, null, e.Type, e.Severity, e.Message, e.JobId, e.CreatedAt);

    public static readonly Expression<Func<JobUpdate, JobItemDto>> JobItem = u => new JobItemDto(
        u.UpdateGuid, u.Title, u.KbNumber, u.Category, u.Status, u.SkipReason, u.ResultCode, u.ErrorCode, u.ErrorMessage, u.RebootRequired,
        u.DownloadPercent, u.StartedAt, u.CompletedAt);

    public static string HistoryResult(int resultCode) => resultCode switch
    {
        0 => "NotStarted",
        1 => "InProgress",
        2 => "Succeeded",
        3 => "SucceededWithErrors",
        4 => "Failed",
        5 => "Aborted",
        _ => "Unknown",
    };

    public static UpdateHistoryDto History(UpdateHistoryEntry h) => new(
        h.Id, h.UpdateGuid, h.Title, h.KbNumber, h.Operation, h.ResultCode, HistoryResult(h.ResultCode), h.HResult,
        h.HResult is { Length: > 0 } code && code != "0x00000000" ? WindowsUpdateErrors.DescribeCode(code) : null, h.Date);

    /// <summary>Intermediate row loaded from SQL; mapped to <see cref="EndpointSummaryDto"/> in memory (OS naming, agent outdated).</summary>
    public sealed record EndpointRow(
        Guid Id, string Hostname, Guid OrganizationId, string OrganizationName, Guid? SiteId, string? SiteName, EndpointStatus Status,
        string? OperatingSystem, string? OsVersion, string? OsBuild, int? OsBuildNumber, string? OsProductType, string? Architecture, string? AgentVersion,
        bool IsOnline, DateTimeOffset? LastCheckInAt, DateTimeOffset? LastScanAt, ComplianceStatus ComplianceStatus, int MissingRequiredCount,
        int MissingTotalCount, int FailedCount, bool RebootPending, OsSupportStatus OsSupportStatus, string? CurrentActivity, string? LastSeenIp);

    public static readonly Expression<Func<EasyPatch.Core.Entities.Endpoint, EndpointRow>> EndpointRowProjection = e => new EndpointRow(
        e.Id, e.Hostname, e.OrganizationId, e.Organization!.Name, e.SiteId, e.Site != null ? e.Site.Name : null, e.Status, e.OperatingSystem,
        e.OsVersion, e.OsBuild, e.OsBuildNumber, e.OsProductType, e.Architecture, e.AgentVersion, e.IsOnline, e.LastCheckInAt, e.LastScanAt,
        e.ComplianceStatus, e.MissingRequiredCount, e.MissingTotalCount, e.FailedCount, e.RebootPending, e.OsSupportStatus, e.CurrentActivity,
        e.LastSeenIp);

    public static EndpointSummaryDto ToSummary(EndpointRow r, Version? latestAgent) => new(
        r.Id, r.Hostname, r.OrganizationId, r.OrganizationName, r.SiteId, r.SiteName, r.Status,
        OsNaming.Describe(r.OperatingSystem, r.OsBuildNumber, r.OsProductType), r.OsVersion, r.OsBuild, r.Architecture, r.AgentVersion,
        IsOutdated(r.AgentVersion, latestAgent), r.IsOnline, r.LastCheckInAt, r.LastScanAt, r.ComplianceStatus, r.MissingRequiredCount,
        r.MissingTotalCount, r.FailedCount, r.RebootPending, r.OsSupportStatus, r.CurrentActivity, r.LastSeenIp);

    public static bool IsOutdated(string? agentVersion, Version? latest) =>
        latest is not null && Version.TryParse(agentVersion, out var v) && v < latest;

    public static Version? ParseVersion(string? value) => Version.TryParse(value, out var v) ? v : null;
}
