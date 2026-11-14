# EasyPatch REST API

Administrator and agent routes are versioned under `/api/v1`. JSON uses camelCase, enums are
serialized as strings, and timestamps are ISO-8601 in UTC.

The live OpenAPI document is served at `/api/openapi/v1.json`, with Swagger UI at
`/api/docs`. Both require a signed-in session (any role).

## Conventions

| Topic | Rule |
|---|---|
| Authentication | Admin routes use the session cookie `__Host-easypatch-session`. Agent routes use signed requests ([security.md](security.md#agent-request-signing)). |
| CSRF | Unsafe admin methods (POST, PUT, PATCH, DELETE) must send `X-XSRF-TOKEN` with the value of the `XSRF-TOKEN` cookie. `POST /auth/login` and `POST /auth/password/reset` are exempt. A missing or wrong header returns `403` with `code=csrf_failed`. |
| Correlation | `X-Correlation-ID` is accepted (max 64 characters of `[A-Za-z0-9-_.]`) or generated. It is returned on every response and included in error bodies and logs. |
| Pagination | Paged routes take `?page=1&pageSize=50` (maximum 200) and return `{ items, page, pageSize, totalCount }`. Organizations, sites, users, tokens, deployment codes and the per-endpoint update lists are returned as plain arrays. |
| Sorting | `GET /endpoints` and `GET /updates` accept `?sort=<field>&dir=asc|desc`; the fields are listed below. Unknown fields fall back to the default order. |
| Errors | RFC 9457 `application/problem+json` with the extensions `code` (machine-readable) and `correlationId`. Validation errors (`400`, `code=validation_failed`) add `errors` (field → messages). Some errors carry extra members, such as `existingJobId` on `409 duplicate_job`. |
| Rate limits | Login and other password/MFA operations: 20/min per IP. Enrollment: 30/min per IP. Agent routes: 120/min per credential. Downloads: 60/min per IP. All other admin routes: 600/min per user. Exceeding a limit returns `429` with `code=rate_limited` and `Retry-After`. The limits can be changed with the `RateLimits:*` settings. |
| Roles | **Viewer** = any role (ReadOnly, Admin, SuperAdmin). **Operator** = Admin or SuperAdmin. **SuperAdmin** = SuperAdmin only. Users restricted to some organizations only see those organizations; anything outside them answers `404`. |

## Agent protocol (`/api/v1/agents`)

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/enroll` | none (token or deployment code in the body) | Register a public key. Linux agents add `platform`, `osId` and `osVersion` to the device description. Returns `Active` (with `endpointId`, `credentialId` = `c_…`, `config`) or `PendingApproval` (with `credentialId` = `r_…`). |
| POST | `/enrollment/status` | signed with `r_…` | Poll a pending enrollment: `PendingApproval`, `Active` (with the new `c_…` credential), `Rejected` or `Expired`. This is the only route an `r_…` credential may call. |
| POST | `/check-in` | signed | Combined heartbeat and job poll. Request: agent version, boot time, reboot state and reasons, IDs of jobs the agent holds, current activity, capabilities, signed-in user and tray counts, the Windows Update policy status (only when it changed, and at least daily), and for self-update the package architecture (`agentArchitecture`, `x64` or `arm64`), the package format (`agentPackageFormat`: `msi`, `deb` or `rpm`; absent before 1.3.0 means `msi`) and the IDs of the release keys built into the agent (`releaseKeyIds`). Response: server time, config (including the tray and notification settings and the effective Windows Update settings), jobs to run (at most one newly leased job, up to five messages, plus re-deliveries), IDs of jobs to cancel, `rotateCredential`, latest available agent version. |
| POST | `/inventory` | signed | OS and hardware inventory. Linux agents also send `platform` (`Linux`; absent means `Windows`), `osId` and `osVersion` from `/etc/os-release`, the kernel version, the package manager and how the private key is protected. `204`. |
| POST | `/updates/scan-results` | signed | Full scan snapshot (missing and installed updates, update source). Linux updates carry a `package` object: name, architecture, installed and new version, repository, `security`, severity and advisories. Idempotent by `scanId`. `204`. |
| POST | `/updates/history` | signed | Windows Update history entries, deduplicated on the server. `204`. |
| POST | `/jobs/{jobId}/ack` | signed | Confirm receipt of a delivered job. `409` with `code=job_cancelled` if it was cancelled or already finished. |
| POST | `/jobs/{jobId}/status` | signed | Progress, per-update results and final status. Each report carries a per-job `sequence`; stale or repeated reports are ignored. `204`. An impossible transition returns `409 invalid_transition`. |
| POST | `/credentials/rotate` | signed (current key) | Register a new public key and return the new `credentialId`. Idempotent per key. The old credential stays valid for one hour. |
| POST | `/unenroll` | signed | Called when the agent is uninstalled (best effort). Marks the endpoint decommissioned and revokes its credentials. `204`. |

Agent authentication failures return `401` with one of these codes: `invalid_signature`,
`clock_skew` (the body includes `serverTime`), `replayed_nonce`, `unknown_credential`,
`credential_revoked`, `credential_expired`, `endpoint_revoked`. An `r_…` credential calling
any route other than `/enrollment/status` gets `403 enrollment_invalid`.

The original spec had separate routes for heartbeat, job polling and install results. These
are merged: heartbeat and job polling are `/check-in`, and install results are part of
`/jobs/{jobId}/status`. This avoids redundant traffic, and every report about a job flows
through one sequence-numbered channel.

## Downloads (anonymous)

| Method | Route | Purpose |
|---|---|---|
| GET | `/downloads/agent/{fileName}` | Agent package (MSI, `.deb` or `.rpm`) from the package directory. Agents download updates here too. |
| GET | `/downloads/install/{deploymentCode}.ps1` | The tokenless install script for a deployment code (`?requireSignature=true` adds an Authenticode check). Returns `404` for an unknown, expired or revoked code and `503` when no agent package is available; both bodies are a PowerShell `throw`, so a piped one-liner fails loudly. |
| GET | `/downloads/install/{deploymentCode}.sh` | The tokenless Linux install script (POSIX `sh`) for a deployment code, with the newest `.deb` and `.rpm` package for each architecture and their SHA-256. `404` for an unknown, expired or revoked code and `503` when no Linux package is available; both bodies are shell that prints the error and exits with status 1. |

These routes are outside `/api/v1`, need no session and are limited to 60 requests per
minute per IP. The install scripts contain no secret: endpoints installed with it wait for
administrator approval.

## Administrator API

### Authentication (`/api/v1/auth`)

| Method | Route | Notes |
|---|---|---|
| POST | `/login` | `{username, password}` → `{mfaRequired, mfaSetupRequired, user}`. Sets the session and `XSRF-TOKEN` cookies. |
| POST | `/mfa/verify` | `{code}` (TOTP or recovery code) completes the sign-in |
| POST | `/logout` | Revokes the current session |
| GET | `/me` | Current user, role and MFA state. Also refreshes the `XSRF-TOKEN` cookie. |
| POST | `/mfa/setup` | Returns the secret, the `otpauth` URI and a QR code as a PNG data URI |
| POST | `/mfa/enable` | `{code}` → 10 recovery codes, shown once |
| POST | `/mfa/disable` | `{password, code}`. Refused (`409 mfa_required`) while the `RequireMfa` server setting is on. |
| POST | `/mfa/recovery-codes` | `{password}` → a new set of recovery codes |
| POST | `/password/change` | `{currentPassword, newPassword}`. Revokes your other sessions. |
| POST | `/password/reset` | `{token, newPassword}`. Anonymous; the token comes from an administrator-issued link. |
| GET | `/sessions` | Your active sessions |
| DELETE | `/sessions/{id}` | Revoke one of your sessions |

Until a user has completed every sign-in step, other routes answer `403` with
`code=mfa_required`, `mfa_setup_required` or `password_change_required`.

### Users (`/api/v1/users`, SuperAdmin)

`GET /`, `POST /` (without a password the response contains a one-time set-password link),
`GET /{id}`, `PUT /{id}` (`email`, `role`, `isActive`), `POST /{id}/reset-link`,
`POST /{id}/mfa/reset`, `POST /{id}/sessions/revoke`, `PUT /{id}/organizations`
(`{organizationIds[]}`; an empty list means all organizations).

### Organizations, sites and policy

Reads are Viewer; writes are Operator. Creating an organization requires a user who is not
restricted to specific organizations.

| Method | Route |
|---|---|
| GET/POST | `/organizations` (`?search=&includeArchived=`; each item includes endpoint, online, offline and missing counts and compliance) |
| GET/PUT/DELETE | `/organizations/{id}` (DELETE returns `409 has_endpoints` while the organization has endpoints) |
| POST | `/organizations/{id}/archive`, `/organizations/{id}/restore` |
| GET/POST | `/organizations/{id}/sites` |
| GET | `/organizations/{id}/endpoints` (same filters as `/endpoints`) |
| GET/PUT/DELETE | `/organizations/{id}/policy` |
| GET/PUT/DELETE | `/sites/{id}` |
| POST | `/sites/{id}/archive`, `/sites/{id}/restore` |
| GET/PUT/DELETE | `/sites/{id}/policy` |
| GET/PUT/DELETE | `/endpoints/{id}/policy` (an endpoint override; GET returns an empty policy when there is none; `maxConcurrentInstallsPerSite` cannot be set here) |
| GET/PUT | `/policies/global` |

A policy body has nullable fields; `null` inherits from the level above. Besides the install
and schedule fields it has `trayIconEnabled`, `installNotificationsEnabled`,
`agentAutoUpdateEnabled` (*Agent updates*: `true` Automatic, `false` Manual; the global default
is `true`), `restartSchedule` (`{enabled, days[0-6], time "HH:mm", condition, mode, countdownMinutes,
deadlineMinutes, expiryMinutes}`) and `windowsUpdate`. `windowsUpdate` has one entry per group
(`automaticUpdates`, `featureUpdates`, `qualityUpdates`, `restartBehavior`), each `null`
(inherit), `{managed: false}` (*Not configured*) or `{managed: true, value: {…}}`. Invalid
values return `400 validation_failed`.

### Endpoints

| Method | Route | Notes |
|---|---|---|
| GET | `/endpoints` | Filters: `organizationId, siteId, search, compliance, online, os, rebootPending, outdatedAgent, status`. Sort: `hostname` (default), `organization, site, os, missing, lastCheckIn, compliance, agentVersion`. |
| GET/PUT/DELETE | `/endpoints/{id}` | GET includes `platform` (`Windows` or `Linux`) and, for Linux, `linux`: `osId`, `osVersion`, `kernelVersion`, `packageManager`, `keyProtection`. PUT: `siteId`, `notes`, `esuOverride`. DELETE revokes the credentials and removes the record. |
| POST | `/endpoints/{id}/revoke` | `{reason?}`. Revokes all credentials; the agent can no longer call the server. |
| GET | `/endpoints/{id}/updates` | `?state=&category=&requiredOnly=`. Missing, failed, pending-reboot, pending-verification and excluded updates, with the live state of an active job overlaid. Linux updates include `package` (name, architecture, version, the endpoint's installed version, repository, advisories). |
| GET | `/endpoints/{id}/installed-updates` | Updates confirmed installed by a scan |
| GET | `/endpoints/{id}/update-history` | Windows Update history (`?search=&page=&pageSize=`). Always empty for Linux endpoints |
| GET | `/endpoints/{id}/events` | Agent and server activity (`?severity=&page=&pageSize=`) |
| GET | `/endpoints/{id}/jobs` | Job history (`?page=&pageSize=`) |
| GET | `/endpoints/{id}/effective-policy` | Resolved policy and where each value came from, including the restart schedule, the Windows Update settings per group and the exact registry values the agent will set |
| GET | `/endpoints/{id}/install-preview` | `?mode=all|selected&updateGuids=<comma-separated>`. What would be installed, what would be skipped and why, total size, reboot implications and warnings. |
| POST | `/endpoints/{id}/install-updates` | `{mode: "AllEligible"|"Selected", updateGuids?, force}` → `202` with the job. `409 duplicate_job` (with `existingJobId`) unless `force`; `409 nothing_to_install` when none of the selected updates can be installed. |
| POST | `/endpoints/{id}/scan` | Queue an `UpdateScan` job (`202`) |
| POST | `/endpoints/{id}/inventory-refresh` | Queue an `InventoryRefresh` job (`202`) |
| POST | `/endpoints/{id}/exclusions` | `{updateGuid, reason?}`. Exclude an update on this endpoint. |
| DELETE | `/endpoints/{id}/exclusions/{updateGuid}` | Remove the exclusion |
| POST | `/endpoints/{id}/restart` | `{mode, condition, countdownMinutes?, deadlineMinutes?, scheduledAt?, expiresAfterMinutes?, force}` → `202` with the job. `mode`: `Immediate`, `Countdown`, `PromptWithDeadline`, `PromptNoDeadline`; `condition`: `Always`, `OnlyIfPending`. `scheduledAt` is at most 31 days ahead; `expiresAfterMinutes` (15 to 1440, default 120) is how long an offline computer may take to pick it up. `409 duplicate_job` while another restart is active, `409 agent_upgrade_required` for agents without restart support. |
| POST | `/endpoints/{id}/notify` | `{preset, restartAt?}` → `202` with the job. `restartAt` is required for `RestartScheduled`. `409 agent_upgrade_required` for agents without message support. |
| POST | `/endpoints/{id}/agent-update` | `{version?}` (send `{}` for the newest) → `202` with an `AgentUpdate` job for the newest signed package for the agent's architecture and package format, or the given version. `409` with `agent_upgrade_required` (agent older than 1.2.0 or built without a release key), `no_agent_package`, `agent_up_to_date` (also for older versions: agents are never downgraded), `package_not_signed`, `release_key_not_trusted` or `duplicate_job`. |
| POST | `/endpoints/bulk/scan`, `/endpoints/bulk/install-updates` | `{endpointIds[], force}`. Returns a per-endpoint outcome. |
| POST | `/endpoints/bulk/restart` | `{endpointIds[], restart: {…as above}}`. Per-endpoint outcome. |
| POST | `/endpoints/bulk/notify` | `{endpointIds[], message: {preset, restartAt?}}`. Per-endpoint outcome. |
| POST | `/endpoints/bulk/agent-update` | `{endpointIds[], version?}`. Per-endpoint outcome with the codes above. |
| GET | `/user-messages` | Viewer. The preset messages with their title and text (`{time}` marks where the restart time goes) |

All POST, PUT and DELETE routes above are Operator. Bulk routes accept at most 1000
endpoints.

### Fleet updates

`GET /updates` aggregates updates across endpoints with missing, failed, installed and
pending-reboot counts. Filters: `organizationId, siteId, category, state, search`. Sort:
`missing` (default: missing + failed), `failed`, `installed`. Without `state`, only updates that
are missing, failed or pending a restart somewhere are listed; `search` matches the title and KB
numbers (Linux update titles contain the package name). Linux updates include `package`. Also `GET /updates/{updateGuid}`
(`?organizationId=&siteId=`), `GET /updates/{updateGuid}/endpoints`
(`?state=&organizationId=&siteId=&page=&pageSize=`) and
`POST /updates/{updateGuid}/install` (Operator; `{organizationId?, siteId?, endpointIds?}`
creates a `Selected` install job on each affected endpoint, at most 1000).

### Jobs

| Method | Route | Notes |
|---|---|---|
| GET | `/jobs` | Filters: `endpointId, organizationId, status, type, trigger, active, from, to, page, pageSize`. Types: `UpdateScan`, `InstallUpdates`, `InventoryRefresh`, `Restart`, `NotifyUser`, `AgentUpdate` |
| GET | `/jobs/{id}` | Includes per-update results and the job's events |
| POST | `/jobs/{id}/cancel` | Operator. Immediate when the job is queued, delivered or waiting for a reboot; otherwise the agent is asked to stop after the current update. A restart that is waiting for users is cancelled at the agent's next check-in, which also aborts a Windows restart that is still in its warning period. |
| DELETE | `/jobs/{id}` | Same as `POST /jobs/{id}/cancel` |
| POST | `/jobs/{id}/retry` | Operator. Creates a new job with the same parameters (`202`); only for finished jobs. |

### Agent management (Operator)

| Method | Route | Notes |
|---|---|---|
| GET/POST | `/enrollment-tokens` | GET: `?organizationId=&includeInactive=`. POST `{organizationId, siteId?, description?, expiresInHours, maxUses, count}` returns each plaintext token **once**, with a ready-made `msiexec` command and `linuxEnrollCommand` (pipes the token into `easypatch-agent enroll --token-file -`). |
| POST | `/enrollment-tokens/{id}/revoke` | |
| GET/POST | `/deployment-codes` | POST `{organizationId, siteId?, name, expiresInDays?, allowedSourceCidrs?, maxPendingRequests?}` → `201`. Each code includes the PowerShell one-liner and the Linux one-liner (`linuxOneLiner`, `linuxScriptUrl`). |
| POST | `/deployment-codes/{id}/revoke` | |
| GET | `/deployment-codes/{id}/install-script` | `?package=&requireSignature=&platform=`. The full PowerShell script, the one-liner, and the package name, version and SHA-256. `platform=linux` returns the Linux shell script and one-liner, with the package names and SHA-256 values comma-separated. |
| GET | `/enrollment-requests` | `?status=Pending&organizationId=&page=&pageSize=` |
| POST | `/enrollment-requests/{id}/approve` | `{organizationId?, siteId?}` (optionally assign elsewhere) |
| POST | `/enrollment-requests/{id}/reject` | `{reason?}` |
| POST | `/enrollment-requests/bulk-approve` | `{ids[]}` |
| GET | `/agent-packages` | Viewer. Packages in the package directory with `format` (`msi`, `deb`, `rpm`), version, size, SHA-256 and, when a valid `.sig` file is next to the package, its `releaseKeyId`. `signatureProblem` says why a `.sig` file was ignored. |
| GET | `/agent-versions` | Viewer. Agent version distribution (`?organizationId=`) |

### Dashboard, reports, audit, settings

| Method | Route | Role |
|---|---|---|
| GET | `/dashboard/summary` | Viewer |
| GET | `/dashboard/activity` (`limit, organizationId, severity`) | Viewer |
| GET | `/reports/compliance` (`organizationId, siteId, endpointId, os, compliance`) and `/reports/compliance.csv` | Viewer |
| GET | `/reports/compliance/trend` (`organizationId, from, to`) | Viewer |
| GET | `/reports/installations` (`from, to, organizationId, siteId, endpointId, category, result, page, pageSize`) and `/reports/installations.csv` | Viewer |
| GET | `/audit-logs` (`userId, organizationId, action` (prefix), `from, to, page, pageSize`) | Operator. Users restricted to some organizations only see entries for those organizations. |
| GET/PUT | `/settings` | GET Viewer, PUT SuperAdmin |
| GET | `/server-info` (version, public URL, server time) | Viewer |
| GET/POST, PUT/DELETE | `/lifecycle`, `/lifecycle/{id}` (OS lifecycle table) | GET Viewer, writes SuperAdmin |

CSV exports are UTF-8 with a byte-order mark. Text values that start with `=`, `+`, `-`, `@`,
tab or carriage return are prefixed with `'` so spreadsheets do not evaluate them as formulas.

### Health

`GET /health/live` reports that the process is up. `GET /health/ready` reports that the
database is reachable. Neither requires authentication. The worker has its own `/health` on
port 8081 inside its container (database plus maintenance-loop heartbeat); it is not exposed
through Caddy.
