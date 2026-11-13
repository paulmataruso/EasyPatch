using EasyPatch.Core.Entities;

namespace EasyPatch.Core.Domain;

/// <summary>One update reported by a scan, already mapped to a catalog row.</summary>
public sealed record ReconcileItem(Guid UpdateGuid, Guid UpdateId, bool RebootRequired, IReadOnlyList<Guid> Supersedes);

public sealed record ReconcileResult(IReadOnlyList<EndpointUpdate> Added, int Changed);

/// <summary>
/// Applies a scan snapshot to an endpoint's update states. Pure logic (no I/O) so it can be unit tested.
/// Rules (docs/architecture.md, "Update state model"):
/// * Installed list → Installed (the only path to Installed).
/// * Missing with WUA RebootRequired → PendingReboot.
/// * Missing and superseded by another reported update → Superseded.
/// * Missing after a reported-successful install (PendingVerification) → Failed ("still missing after install").
/// * Missing and previously Failed → stays Failed (error kept); otherwise Missing.
/// * Excluded overrides Missing/Failed/Superseded.
/// * Rows absent from both lists: Installed stays Installed (WUA drops old installed updates from results);
///   other rows become Superseded (if superseded) or NotApplicable.
/// </summary>
public static class UpdateReconciler
{
    public const string StillMissingAfterInstall =
        "Installation was reported as successful, but Windows Update still reports the update as missing.";

    public static ReconcileResult Reconcile(
        Guid endpointId,
        IDictionary<Guid, EndpointUpdate> existing,
        IReadOnlyList<ReconcileItem> missing,
        IReadOnlyList<ReconcileItem> installed,
        Func<Guid, bool> isExcluded,
        Func<Guid, Guid, bool> isRequired,
        Guid scanId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(isExcluded);
        ArgumentNullException.ThrowIfNull(isRequired);

        var added = new List<EndpointUpdate>();
        var changed = 0;
        var superseded = missing.Concat(installed).SelectMany(i => i.Supersedes).ToHashSet();
        var seen = new HashSet<Guid>();

        EndpointUpdate GetOrAdd(ReconcileItem item, EndpointUpdateState initial)
        {
            if (!existing.TryGetValue(item.UpdateGuid, out var row))
            {
                row = new EndpointUpdate
                {
                    Id = Guid.CreateVersion7(now),
                    EndpointId = endpointId,
                    UpdateGuid = item.UpdateGuid,
                    UpdateId = item.UpdateId,
                    State = initial,
                    FirstDetectedAt = now,
                    LastDetectedAt = now,
                    StateChangedAt = now,
                };
                existing[item.UpdateGuid] = row;
                added.Add(row);
            }

            row.UpdateId = item.UpdateId;
            row.LastDetectedAt = now;
            row.LastScanId = scanId;
            return row;
        }

        void SetState(EndpointUpdate row, EndpointUpdateState state)
        {
            if (row.State != state)
            {
                row.State = state;
                row.StateChangedAt = now;
                changed++;
            }
        }

        foreach (var item in installed)
        {
            if (!seen.Add(item.UpdateGuid))
            {
                continue;
            }

            var row = GetOrAdd(item, EndpointUpdateState.Installed);
            if (row.State != EndpointUpdateState.Installed)
            {
                row.InstalledAt ??= now;
            }

            SetState(row, EndpointUpdateState.Installed);
            row.ErrorCode = null;
            row.ErrorMessage = null;
            row.IsExcluded = isExcluded(item.UpdateGuid);
            row.IsRequired = isRequired(item.UpdateGuid, item.UpdateId);
        }

        foreach (var item in missing)
        {
            if (!seen.Add(item.UpdateGuid))
            {
                continue;
            }

            var row = GetOrAdd(item, EndpointUpdateState.Missing);
            var excluded = isExcluded(item.UpdateGuid);
            row.IsExcluded = excluded;
            row.IsRequired = isRequired(item.UpdateGuid, item.UpdateId);

            EndpointUpdateState next;
            if (item.RebootRequired)
            {
                next = EndpointUpdateState.PendingReboot;
                row.ErrorCode = null;
                row.ErrorMessage = null;
            }
            else if (excluded)
            {
                next = EndpointUpdateState.Excluded;
            }
            else if (superseded.Contains(item.UpdateGuid))
            {
                next = EndpointUpdateState.Superseded;
            }
            else if (row.State == EndpointUpdateState.PendingVerification)
            {
                next = EndpointUpdateState.Failed;
                row.ErrorCode = null;
                row.ErrorMessage = StillMissingAfterInstall;
            }
            else if (row.State == EndpointUpdateState.Failed && row.FailedAttempts > 0)
            {
                next = EndpointUpdateState.Failed;
            }
            else
            {
                next = EndpointUpdateState.Missing;
            }

            SetState(row, next);
        }

        foreach (var row in existing.Values)
        {
            if (seen.Contains(row.UpdateGuid))
            {
                continue;
            }

            switch (row.State)
            {
                case EndpointUpdateState.Installed:
                case EndpointUpdateState.NotApplicable:
                    break;
                case EndpointUpdateState.Superseded:
                    break;
                default:
                    SetState(row, superseded.Contains(row.UpdateGuid)
                        ? EndpointUpdateState.Superseded
                        : EndpointUpdateState.NotApplicable);
                    break;
            }
        }

        return new ReconcileResult(added, changed);
    }

    /// <summary>Applies a final per-update install outcome reported by the agent.</summary>
    public static void ApplyInstallOutcome(
        EndpointUpdate row, bool succeeded, bool rebootRequired, string? errorCode, string? errorMessage, Guid jobId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.LastJobId = jobId;
        row.LastAttemptAt = now;
        var next = succeeded
            ? (rebootRequired ? EndpointUpdateState.PendingReboot : EndpointUpdateState.PendingVerification)
            : EndpointUpdateState.Failed;
        if (succeeded)
        {
            row.ErrorCode = null;
            row.ErrorMessage = null;
            row.FailedAttempts = 0;
        }
        else
        {
            row.ErrorCode = errorCode;
            row.ErrorMessage = errorMessage;
            row.FailedAttempts++;
        }

        if (row.State != next)
        {
            row.State = next;
            row.StateChangedAt = now;
        }
    }

    /// <summary>States that count as "not installed yet" for compliance (when the update is required).</summary>
    public static bool IsOutstanding(EndpointUpdateState state) =>
        state is EndpointUpdateState.Missing or EndpointUpdateState.Failed or EndpointUpdateState.PendingVerification;
}
