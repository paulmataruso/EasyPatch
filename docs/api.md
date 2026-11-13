# EasyPatch REST API

All routes are versioned under `/api/v1`. JSON uses camelCase, enums are serialized as
strings, and timestamps are ISO-8601 in UTC.

The live OpenAPI document is served at `/api/openapi/v1.json`, with Swagger UI at
`/api/docs`. Both require an administrator session.

## Conventions

| Topic | Rule |
|---|---|
| Authentication | Admin routes use the session cookie `__Host-easypatch-session`. Agent routes use signed requests ([security.md](security.md#agent-request-signing)). |
| CSRF | Unsafe admin methods must send `X-XSRF-TOKEN`, whose value comes from the `XSRF-TOKEN` cookie. |
| Correlation | `X-Correlation-ID` is accepted (max 64 characters `[A-Za-z0-9-_.]`) or generated. It is returned on every response and included in error bodies and logs. |
| Pagination | `?page=1&pageSize=50` (maximum 200). The response is `{ items, page, pageSize, totalCount }`. |
| Sorting | `?sort=<field>&dir=asc|desc`. Fields come from a per-route allow-list. |
| Errors | RFC 9457 `application/problem+json` with extensions `code` (machine-readable), `correlationId` and, for validation errors, `errors`. |
| Rate limits | Login and enrollment: 10/min per IP. Agent routes: 120/min per credential. Admin API: 600/min per user. Exceeding a limit returns `429` with `Retry-After`. |

## Agent protocol (`/api/v1/agents`)

| Method | Route | Auth | Purpose |
|---|---|---|---|
| POST | `/enroll` | none (token or deployment code in body) | Register a public key. Returns `Active` (with `endpointId`, `credentialId`, `config`) or `PendingApproval` (with `credentialId` = `r_…`). |
| POST | `/enrollment/status` | signed with `r_…` | Poll a pending enrollment: `PendingApproval`, `Active` (new `c_…` credential), `Rejected` or `Expired` |
| POST | `/check-in` | signed | Combined heartbeat and job poll. Request: agent version, boot time, reboot state, active job IDs. Response: server time, config, up to one newly leased job, re-delivered jobs, IDs of jobs to cancel, `rotateCredential`, latest agent version. |
| POST | `/inventory` | signed | OS and hardware inventory |
| POST | `/updates/scan-results` | signed | Full scan snapshot (missing and installed updates, update source). Idempotent by `scanId`. |
| POST | `/updates/history` | signed | Windows Update history entries (deduplicated server-side) |
| POST | `/jobs/{jobId}/ack` | signed | Confirm receipt of a delivered job. Returns `409` with `code=job_cancelled` if it was cancelled. |
| POST | `/jobs/{jobId}/status` | signed | Progress, per-update results and final status. Sequence-numbered and idempotent. |
| POST | `/credentials/rotate` | signed (current key) | Register a new public key. Returns the new `credentialId`. Idempotent per key. |
| POST | `/unenroll` | signed | Called by the MSI on uninstall (best effort). Marks the endpoint decommissioned and revokes its credentials. |

The original spec had separate routes for heartbeat, job polling and install results. These
are merged: heartbeat and job polling are now `/check-in`, and install results are part of
`/jobs/{jobId}/status`. This avoids redundant traffic, and every report about a job flows
through one sequence-numbered channel.

## Administrator API

### Authentication (`/api/v1/auth`)

| Method | Route | Notes |
|---|---|---|
| POST | `/login` | `{username, password}` → `{mfaRequired, mfaSetupRequired, user}` |
| POST | `/mfa/verify` | `{code}` (TOTP or recovery code) completes login |
| POST | `/logout` | Revokes the current session |
| GET | `/me` | Current user, role and MFA state. Also refreshes the `XSRF-TOKEN` cookie. |
| POST | `/mfa/setup` | Returns the secret, `otpauth` URI and a QR PNG data URI |
| POST | `/mfa/enable` | `{code}` → recovery codes, shown once |
| POST | `/mfa/disable` | `{password, code}`. Not allowed when `RequireMfa` is on. |
| POST | `/mfa/recovery-codes` | `{password}` → generates a new set |
| POST | `/password/change` | `{currentPassword, newPassword}` and revokes your other sessions |
| POST | `/password/reset` | `{token, newPassword}` (anonymous; uses an administrator-issued link) |
| GET | `/sessions` | Your sessions |
| DELETE | `/sessions/{id}` | Revoke one session |

### Users (`/api/v1/users`, SuperAdmin only)

`GET /`, `POST /`, `GET /{id}`, `PUT /{id}` (role, email, active), `POST /{id}/reset-link`,
`POST /{id}/mfa/reset`, `POST /{id}/sessions/revoke`, `PUT /{id}/organizations` (org scope).

### Organizations and sites

| Method | Route |
|---|---|
| GET/POST | `/organizations` (list includes endpoint, online, offline and missing counts, compliance and last activity) |
| GET/PUT/DELETE | `/organizations/{id}` (DELETE only when the organization has no endpoints) |
| POST | `/organizations/{id}/archive`, `/organizations/{id}/restore` |
| GET/POST | `/organizations/{id}/sites` |
| GET | `/organizations/{id}/endpoints` (same as `/endpoints?organizationId=`) |
| GET/PUT/DELETE | `/organizations/{id}/policy` |
| GET/PUT/DELETE | `/sites/{id}` |
| POST | `/sites/{id}/archive`, `/sites/{id}/restore` |
| GET/PUT/DELETE | `/sites/{id}/policy` |
| GET/PUT | `/policies/global` |

### Endpoints

| Method | Route | Notes |
|---|---|---|
| GET | `/endpoints` | Filters: `organizationId, siteId, search, compliance, online, os, rebootPending, outdatedAgent`. Sort: `hostname, organization, site, os, missing, lastCheckIn, compliance, agentVersion`. |
| GET/PUT/DELETE | `/endpoints/{id}` | PUT: `siteId`, `notes`, `esuOverride`. DELETE removes the record and revokes its credentials. |
| POST | `/endpoints/{id}/revoke` | Revoke all credentials |
| GET | `/endpoints/{id}/updates` | `?state=&category=&requiredOnly=`. Available updates with live job overlay. |
| GET | `/endpoints/{id}/installed-updates` | Updates confirmed installed by a scan |
| GET | `/endpoints/{id}/update-history` | Windows Update history |
| GET | `/endpoints/{id}/events` | Agent activity |
| GET | `/endpoints/{id}/jobs` | Job history |
| GET | `/endpoints/{id}/effective-policy` | Resolved policy and where each value came from |
| GET | `/endpoints/{id}/install-preview` | `?mode=all|selected&updateGuids=`. Lists what would be installed: counts, size, reboot implications and warnings. |
| POST | `/endpoints/{id}/install-updates` | `{mode: "AllEligible"|"Selected", updateGuids?, force?}`. Returns `409 duplicate_job` with `existingJobId` unless `force`. |
| POST | `/endpoints/{id}/scan` | Queue an `UpdateScan` job |
| POST | `/endpoints/{id}/inventory-refresh` | Queue an `InventoryRefresh` job |
| POST/DELETE | `/endpoints/{id}/exclusions[/{updateGuid}]` | Exclude or include updates for this endpoint |
| POST | `/endpoints/bulk/scan`, `/endpoints/bulk/install-updates` | `{endpointIds[]}`. Returns a per-endpoint outcome. |

### Fleet updates (Update Management)

`GET /updates` (aggregated across endpoints: affected, failed, installed and pending-reboot
counts. Filters: `organizationId, siteId, category, state, search`), `GET /updates/{updateGuid}`,
`GET /updates/{updateGuid}/endpoints?state=`, `POST /updates/{updateGuid}/install`
(`{organizationId?, siteId?, endpointIds?}` creates `Selected` install jobs on affected
endpoints).

### Jobs

`GET /jobs` (filters `endpointId, organizationId, status, type, trigger, from, to`),
`GET /jobs/{id}` (includes per-update results), `DELETE /jobs/{id}` (cancel: immediate when
queued, otherwise requested from the agent), `POST /jobs/{id}/retry`.

### Agent management

| Method | Route | Notes |
|---|---|---|
| GET/POST | `/enrollment-tokens` | POST `{organizationId, siteId?, description, expiresInHours, maxUses, count}` returns the plaintext tokens **once**, plus `msiexec` commands |
| POST | `/enrollment-tokens/{id}/revoke` | |
| GET/POST | `/deployment-codes` | POST `{organizationId, siteId?, name, expiresInDays?, allowedSourceCidrs?, maxPendingRequests?}` |
| POST | `/deployment-codes/{id}/revoke` | |
| GET | `/deployment-codes/{id}/install-script` | `?package=&requireSignature=`. Returns a PowerShell script and a one-liner. |
| GET | `/enrollment-requests` | `?status=Pending` |
| POST | `/enrollment-requests/{id}/approve` | `{organizationId?, siteId?}` (optionally reassign) |
| POST | `/enrollment-requests/{id}/reject` | `{reason}` |
| POST | `/enrollment-requests/bulk-approve` | `{ids[]}` |
| GET | `/agent-packages` | MSIs in `data/agent-packages` with version, size and SHA-256 |
| GET | `/agent-versions` | Version distribution across endpoints |
| GET | `/downloads/agent/{fileName}` | **Anonymous** MSI download, used by the PowerShell installer |

### Dashboard, reports, audit, settings

| Method | Route |
|---|---|
| GET | `/dashboard/summary`, `/dashboard/activity` |
| GET | `/reports/compliance` (`organizationId, siteId, endpointId, os, category`), `/reports/compliance.csv` |
| GET | `/reports/compliance/trend` (`organizationId, from, to`) |
| GET | `/reports/installations` (`from, to, organizationId, siteId, endpointId, category, result`), `/reports/installations.csv` |
| GET | `/audit-logs` (`userId, organizationId, action, from, to`) |
| GET/PUT | `/settings` (PUT is SuperAdmin only) |
| GET/POST/PUT/DELETE | `/lifecycle[/{id}]` (OS lifecycle table; writes are SuperAdmin only) |

### Health

`GET /health/live` reports that the process is up. `GET /health/ready` reports that the
database is reachable. Neither requires authentication.
