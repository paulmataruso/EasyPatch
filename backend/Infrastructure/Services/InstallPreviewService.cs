using EasyPatch.Contracts;
using EasyPatch.Contracts.Agent;
using EasyPatch.Contracts.Updates;
using EasyPatch.Core.Domain;
using EasyPatch.Core.Entities;
using EasyPatch.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EasyPatch.Infrastructure.Services;

public sealed record PreviewItem(
    Guid UpdateGuid, int RevisionNumber, string Title, string? KbNumber, UpdateCategory Category, long? SizeBytes,
    int? RebootBehavior, EndpointUpdateState State);

public sealed record PreviewSkip(Guid UpdateGuid, string Title, string? KbNumber, UpdateCategory Category, string Reason);

public sealed record InstallPreview(
    InstallMode Mode,
    IReadOnlyList<PreviewItem> Items,
    IReadOnlyList<PreviewSkip> Skipped,
    long TotalSizeBytes,
    int RebootLikelyCount,
    IReadOnlyList<string> Warnings,
    InstallFilterOptions Filter,
    DateTimeOffset? LastScanAt);

/// <summary>Computes what an install job would do, using the same <see cref="InstallFilter"/> the agent applies
/// to its fresh scan, so the confirmation dialog matches agent behavior.</summary>
public sealed class InstallPreviewService(EasyPatchDbContext db, PolicyService policies, SettingsService settings, TimeProvider clock)
{
    public async Task<InstallPreview> BuildAsync(Endpoint endpoint, InstallMode mode, IReadOnlyCollection<Guid> selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(selected);
        var now = clock.GetUtcNow();
        var policy = await policies.ResolveAsync(endpoint, ct);
        var exclusions = await db.UpdateExclusions.AsNoTracking().Where(x => x.EndpointId == endpoint.Id).Select(x => x.UpdateGuid).ToListAsync(ct);
        var filter = policy.ToFilter(exclusions);
        var payload = new InstallUpdatesPayload { Mode = mode, SelectedUpdateGuids = selected.ToArray(), Filter = filter };

        var candidates = await db.EndpointUpdates.AsNoTracking().Include(u => u.Update)
            .Where(u => u.EndpointId == endpoint.Id &&
                        (u.State == EndpointUpdateState.Missing || u.State == EndpointUpdateState.Failed ||
                         u.State == EndpointUpdateState.Excluded ||
                         (mode == InstallMode.Selected && selected.Contains(u.UpdateGuid))))
            .ToListAsync(ct);

        var items = new List<PreviewItem>();
        var skipped = new List<PreviewSkip>();
        foreach (var row in candidates.Where(c => c.Update is not null).OrderBy(c => c.Update!.Category).ThenBy(c => c.Update!.Title))
        {
            var u = row.Update!;
            var decision = InstallFilter.Evaluate(row.UpdateGuid, u.KbNumbers, u.Category, u.IsOptional,
                row.State == EndpointUpdateState.Installed, row.State == EndpointUpdateState.PendingReboot, payload);
            if (decision.Install)
            {
                items.Add(new PreviewItem(row.UpdateGuid, u.RevisionNumber, u.Title, u.PrimaryKb, u.Category, u.MaxDownloadSizeBytes, u.RebootBehavior, row.State));
            }
            else if (mode == InstallMode.Selected ? selected.Contains(row.UpdateGuid) : decision.SkipReason != SkipReasons.NotSelected)
            {
                skipped.Add(new PreviewSkip(row.UpdateGuid, u.Title, u.PrimaryKb, u.Category, decision.SkipReason ?? "Unknown"));
            }
        }

        if (mode == InstallMode.Selected)
        {
            var known = candidates.Select(c => c.UpdateGuid).ToHashSet();
            foreach (var guid in selected.Where(g => !known.Contains(g)))
            {
                skipped.Add(new PreviewSkip(guid, "(unknown update)", null, UpdateCategory.Other, SkipReasons.NotApplicable));
            }
        }

        var server = await settings.GetAsync(ct);
        return new InstallPreview(
            mode,
            items,
            skipped,
            items.Sum(i => i.SizeBytes ?? 0),
            items.Count(i => i.RebootBehavior is 1 or 2 || i.Category is UpdateCategory.Cumulative or UpdateCategory.ServicingStack or UpdateCategory.DotNet),
            BuildWarnings(endpoint, policy, server, now),
            filter,
            endpoint.LastScanAt);
    }

    private static List<string> BuildWarnings(Endpoint endpoint, EffectivePolicy policy, ServerSettings server, DateTimeOffset now)
    {
        var warnings = new List<string>();
        if (endpoint.LastCheckInAt is null || now - endpoint.LastCheckInAt > TimeSpan.FromMinutes(server.OfflineThresholdMinutes))
        {
            warnings.Add("The endpoint is offline. The job will stay queued until it checks in (or expires).");
        }

        if (endpoint.LastScanAt is null)
        {
            warnings.Add("The endpoint has not completed an update scan yet. The agent scans again before installing.");
        }
        else if (now - endpoint.LastScanAt > TimeSpan.FromHours(policy.ScanStaleAfterHours))
        {
            warnings.Add("The last update scan is stale. The agent performs a fresh scan before installing, so the final list may differ.");
        }

        if (endpoint.RebootPending)
        {
            warnings.Add("A restart is already pending. Windows may refuse some installations until the endpoint restarts.");
        }

        if (string.Equals(endpoint.UpdateSourceType, nameof(UpdateSourceType.Wsus), StringComparison.Ordinal))
        {
            warnings.Add($"Windows Update is managed by WSUS ({endpoint.UpdateSourceServer}). Only updates approved on that server can be installed.");
        }

        if (endpoint.WufbPoliciesConfigured)
        {
            warnings.Add("Windows Update for Business deferral policies are configured on this endpoint. Installing now may install updates earlier than the deferral rings intend.");
        }

        if (endpoint.SystemDriveFreeBytes is { } free && free < 10L * 1024 * 1024 * 1024)
        {
            warnings.Add($"Low free space on the system drive ({free / (1024 * 1024 * 1024.0):0.0} GB). Cumulative updates can need 10 GB or more.");
        }

        if (endpoint.OsSupportStatus == OsSupportStatus.Unsupported)
        {
            warnings.Add("The operating system version is out of support and no longer receives security updates.");
        }

        return warnings;
    }
}
