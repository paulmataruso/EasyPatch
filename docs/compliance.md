# Compliance

EasyPatch assigns every active endpoint a **compliance status**, computed from the endpoint's
last successful update scan and its restart state. The rules live in one place
(`backend/Core/Domain/ComplianceCalculator.cs`). They are unit tested, and the dashboard,
endpoint lists, reports and daily snapshots all use them.

## Statuses

The server checks the rules in this order and the first match wins:

| # | Status | Condition |
|---|---|---|
| 1 | `NeverScanned` | The agent has not yet reported a successful scan |
| 2 | `ScanStale` | The last successful scan is older than the policy's *scan considered stale after* (default 48 hours) |
| 3 | `FailedUpdates` | At least one **required** update is in state `Failed` |
| 4 | `MissingUpdates` | At least one **required** update is `Missing`, `Failed` or `PendingVerification` |
| 5 | `RebootRequired` | Windows reports a pending restart, or an update is installed and waiting for a restart (`PendingReboot`) |
| 6 | `Compliant` | None of the above |

The order is deliberate:

* Data that is old or missing is never reported as compliant.
* A failure is reported ahead of a merely missing update.
* An endpoint with every required update installed, but still waiting for a restart, is
  `RebootRequired`, not `Compliant`. An update is not secured until the restart completes.

**Being offline does not change compliance by itself.** Offline status is tracked separately.
An endpoint that stays offline drifts to `ScanStale` once its last scan ages past the threshold.
The worker re-checks staleness every minute.

## Required updates

An update is **required** for an endpoint when its effective policy would install it
automatically, meaning all of the following:

* its category is in the policy's install categories. The default is everything except drivers
  and feature updates: Security, Critical, Cumulative, Definition, Servicing Stack, .NET and Other;
* it is not optional ("browse-only" in Windows Update), unless the policy includes optional
  updates (off by default);
* it is not excluded by the policy (by KB number or by update).

Updates that are not required are still shown and can still be installed, but they never affect
compliance. When a policy changes, the required flags and statuses of every endpoint in its
scope are recalculated immediately.

## Update states and verification

Each endpoint/update pair has a state. The full model is in
[architecture.md](architecture.md#update-state-model-two-layers). The parts that matter for
compliance:

* **Only a scan marks an update `Installed`.** A successful install result from the agent moves
  the update to `PendingVerification`, or to `PendingReboot` when Windows asks for a restart.
  These states still count as not installed.
* If the next scan still reports a `PendingVerification` update as missing, it becomes `Failed`
  with the message "Installation was reported as successful, but Windows Update still reports
  the update as missing."
* After an install job, the agent scans again. If a restart is needed, it scans again after the
  computer restarts. **EasyPatch never restarts a computer.** An administrator or the customer
  must restart it.
* `Superseded`, `NotApplicable` and `Excluded` updates never count.

## Operating system support status

Whether the operating system itself is still serviced is reported **separately**, as the
endpoint's *OS support status*. It does not change the compliance status:

| Status | Meaning |
|---|---|
| `Supported` | Within the end-of-servicing date for this product, build and edition group |
| `ExtendedSecurityUpdates` | Past end of servicing, but covered by an ESU program that has not ended. The endpoint must have an ESU license detected by the agent, or an administrator must mark it as enrolled |
| `Unsupported` | Past end of servicing and not covered by ESU |
| `Unknown` | The build or edition is not in the lifecycle table |

The dates come from an editable table under **Settings → OS lifecycle**. It is seeded with
Microsoft's published dates. Verify those against Microsoft's lifecycle pages and keep them up
to date. Windows 10 22H2 is **not** treated as supported after 2025-10-14 unless ESU applies.

A fully patched Windows 10 22H2 machine without ESU is therefore `Compliant`, because nothing
Windows Update offers is missing, and also `Unsupported`, because Windows Update no longer
offers it security fixes. Reports show both columns.

## Percentages and snapshots

* **Compliance percentage** = compliant endpoints ÷ active endpoints × 100, rounded to one
  decimal place. Revoked and decommissioned endpoints are excluded. With no active endpoints the
  percentage is 0.
* **Daily snapshots.** Every 30 minutes the worker records, for each organization and the current
  UTC date, the number of endpoints in each status, the number offline, and the total number of
  missing required updates. The last write of the day stands as that day's figure. Snapshots
  drive the trend chart and are kept for the audit-log retention period (default 365 days).

## Reports

Under **Reports**, and through the API (see [api.md](api.md#dashboard-reports-audit-settings)):

* **Compliance**: one row per active endpoint: hostname, organization, site, operating system
  and build, compliance status, missing required, missing total and failed counts, restart
  pending, online, OS support status, last scan and last check-in. It can be filtered by
  organization, site, endpoint, OS and compliance status, and exported as CSV.
* **Compliance trend**: the daily snapshots for a date range (default: the last 30 days), summed
  over all permitted organizations or for one organization.
* **Installations**: one row per update in each EasyPatch install job: result, skip reason, error
  code and message, whether a restart was required, and who requested the job. It can be
  exported as CSV, up to 50,000 rows.

Tenant-restricted users only see the organizations they are assigned to, in reports and in CSV
exports.
