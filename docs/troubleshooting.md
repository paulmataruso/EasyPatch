# Troubleshooting

For agent-side problems (enrollment, connectivity, Windows Update errors on the endpoint), see
[agent-installation.md](agent-installation.md#troubleshooting). This page covers the server and
the console.

## First checks

```bash
docker compose ps                       # every service should be "running (healthy)"; migrate "exited (0)"
docker compose logs --tail=200 api      # JSON logs, one object per line
docker compose logs --tail=200 worker
docker compose logs --tail=200 caddy
docker compose logs migrate
```

The API answers `GET /health/live` (process up) and `GET /health/ready` (database reachable)
through Caddy:

```bash
curl -s https://patch.example.com/health/ready
```

The worker's `/health` is internal only and reports the database and the heartbeat of each
maintenance loop. A loop is reported as stalled if it has not completed a cycle within three
intervals plus two minutes. `docker compose ps` shows the result as the worker's health.

Every API response carries an `X-Correlation-ID` header. The same ID appears in the API log
entries for that request, and in the body of error responses. Quote it when you report a
problem.

## Server

| Symptom | Cause and fix |
|---|---|
| `docker compose` fails with "Set EASYPATCH_DOMAIN in .env" or "Set ACME_EMAIL in .env" | `.env` is missing or incomplete. Run `scripts/init-secrets.sh`, then edit `.env` |
| `docker compose` fails because `secrets/db_password` does not exist | Run `scripts/init-secrets.sh` |
| `migrate` exits non-zero with an authentication error | `secrets/db_password` changed after the database volume was created. PostgreSQL reads the password only when it initializes an empty volume. Restore the original file, or change the database user's password to match |
| `api` or `worker` never become healthy | Read their logs. The usual causes are that the database is unreachable (check `postgres` health) or `migrate` failed. `api` and `worker` do not start until `migrate` succeeds |
| Browser shows a certificate error, or Caddy logs ACME failures | DNS for `EASYPATCH_DOMAIN` must point at this host, and TCP 80 and 443 must be reachable from the internet. Caddy retries automatically; watch `docker compose logs -f caddy` |
| Audit log shows a Docker address instead of the real client IP | `EASYPATCH_EDGE_SUBNET` does not match the `edge` network's subnet. The API only trusts `X-Forwarded-For` from that subnet |
| `429 Too Many Requests` | A rate limit was hit (see [installation.md](installation.md#rate-limits-and-other-configuration)). Many agents behind one NAT address share the per-IP limits for enrollment and downloads. The per-agent limit applies per credential |
| Disk usage grows | Check retention under **Settings → Server settings → Data retention**. Container logs rotate at 20 MB × 5 files per service |

## Signing in

| Symptom | Fix |
|---|---|
| "Invalid username or password" for a correct password | The account may be locked after five failed attempts. The lockout starts at 15 minutes and doubles with each lockout, up to 24 hours. Wait, or run `docker compose run --rm api unlock --username <name>` |
| Authenticator codes are rejected | The authenticator device's clock is wrong (codes are valid for ±30 seconds), or the code was already used (each code works once). Use a recovery code, or have a SuperAdmin reset MFA under **Settings → Users** |
| Lost MFA device and no recovery codes | Another SuperAdmin resets MFA under **Settings → Users**. If none can sign in: `docker compose run --rm api reset-mfa --username <name>` |
| Forgotten password | A SuperAdmin creates a reset link under **Settings → Users**, or on the host: `docker compose run --rm api reset-password --username <name>` |
| Signed out unexpectedly | Sessions end after the idle timeout (default 30 minutes) or the maximum session length (default 12 hours). Both are under **Settings → Server settings** |
| `403` with code `csrf_failed` | The page was open across a sign-out or session change. Reload it |
| All MFA users must re-enroll after a restore or migration | The Data Protection key ring (`dpkeys` volume) was not restored with the database. Restore `dpkeys.tar` from the same backup |

## Endpoints and agents

| Symptom | Fix |
|---|---|
| Endpoint shows **Offline** | No check-in for longer than *Offline after* (default 10 minutes). Check the agent (`EasyPatch.Agent.exe status`) and its network path. Jobs are not discarded while an endpoint is offline. They wait until the job expires |
| No **Pending approvals** appear for a deployment code | The agent cannot reach the server, the code was revoked, or the computer's IP is outside the code's allowed networks (CIDR). See the agent log |
| Agent requests fail with `clock_skew` | The endpoint's clock is more than 5 minutes off. The agent corrects using the server time in the error, but fixing the time source (NTP or domain time) is the real fix |
| Agent requests fail with `credential_revoked` or `endpoint_revoked` | The endpoint was revoked or removed in the console. Re-enroll the agent with `enroll --force` |
| Agent requests fail with `credential_expired` | The credential passed its lifetime without rotating, for example a computer that was off for a year. Re-enroll the agent |
| No agent packages under **Agent Management → Packages** | Check the file names (`EasyPatchAgent-<version>-x64.msi` or `-arm64.msi`) and that the files are world-readable. The API logs a warning for each `.msi` it ignores. The list refreshes at most once a minute |
| A package shows **not signed** or **invalid** under **Agent Management → Packages** | Agents update only to packages with a valid `<msi>.sig` next to them. *Not signed*: copy the `.sig` file from the build. *Invalid*: hover over the badge for the reason; usually the MSI was rebuilt or Authenticode-signed after the `.sig` was made. Sign it again with `easypatch-release sign` |
| Agents do not update themselves | Only agents 1.2.0 and later built with a release key can (the endpoint's overview says so). The *Agent updates* policy must be Automatic for automatic updates, the endpoint must be online, and the package must be signed with a key the agent trusts. At most 25 agent updates run at a time; a failed update waits 6 hours, and after 3 failures of the same version only **Update agent…** retries it |
| `409 agent_upgrade_required` on **Update agent…** | The agent is older than 1.2.0, or was built without a release key. Install the new MSI on it once |
| `409 release_key_not_trusted` | The newest package was signed with a different release key than the one built into the agent. Sign it with the original key, or install an agent built with the new key manually. See [security.md](security.md#agent-release-signing) |

## Jobs

A job moves through `Queued` → `Delivered` → `Acknowledged` → `Running`, and ends in
`Completed`, `Failed`, `Cancelled` or `Expired`. An install that needs a restart waits in
`WaitingForReboot`. See [architecture.md](architecture.md#4-job-system).

| Symptom | Explanation |
|---|---|
| Job stays `Queued` | An endpoint runs one job at a time. The job waits if the endpoint is offline, is already running a job, or has an install waiting for a restart (only scans and inventory run in that state). Install jobs also wait for the site's *max concurrent installs* (policy, default 10) and the global limit (default 50). Unpicked jobs expire after the job expiry (default 168 hours) |
| `409 duplicate_job` when installing | The endpoint already has an active install job. The response includes `existingJobId`. Cancel it, or use **force** |
| `409 nothing_to_install` | The last scan shows no update that matches the selection or policy: they are installed, excluded or no longer offered. Run a scan first if the data is old |
| Job is `WaitingForReboot` | Windows needs a restart before it can install the remaining updates. Installing never restarts a computer. Restart it yourself or with **Restart…** on the endpoint (or a restart schedule in policy). The agent then continues the job. If no restart happens within 30 days, the job fails with `reboot_not_performed` |
| `409 agent_upgrade_required` on **Restart…** or **Send message…** | The endpoint runs an agent version without restart or message support (before 1.1.0). Upgrade the agent |
| Agent update job `Failed` | The job message gives the reason and the code (`update_untrusted_key`, `update_signature_invalid`, `update_hash_mismatch`, `update_download_failed`, `update_install_failed`, `update_install_timeout`). See [agent-installation.md](agent-installation.md#troubleshooting) |
| Agent update job stays `Running` (*Installing*) | Windows Installer is replacing the agent. The job completes when the new version checks in, usually within a minute. If the computer is turned off during the update, the job completes or fails when it is back |
| Restart job stays `Running` | It is waiting for a signed-in user (prompt) or for its countdown or deadline. The job's progress shows which. A prompt without a deadline waits up to 7 days |
| Restart job `Failed` with `restart_not_performed` | Windows did not restart within 30 minutes of the request (a user or application may have cancelled the shutdown), or nobody accepted a prompt without a deadline within 7 days |
| Message job `Failed` with `no_recipients` | Nobody was signed in, or the EasyPatch tray was not running in the signed-in sessions. The endpoint's overview shows signed-in users and trays |
| Scheduled restarts do not happen | The restart schedule must be enabled in policy, the agent must support restarts, and with *only if a restart is pending* endpoints without a pending restart are skipped. A computer that is off for the whole expiry period is skipped for that occurrence |
| Job `Failed`: "did not acknowledge the job" | The job was delivered 5 times without acknowledgement. The agent is likely stuck or unable to report back. Check the agent log |
| Job `Failed` after the running timeout | No progress for 12 hours (default). If the agent reports a result later, the late result is still accepted |
| Update `Failed` with an `0x8…` error code | Windows Update's own error, shown with its description on the job and the update. Common causes: servicing store corruption (`0x80073712`), not enough disk space, or an unreachable update source |
| Update `Failed`: "still reports the update as missing" | The agent reported success but the verification scan disagrees. Retry the job. If it fails again, check the endpoint's Windows Update log |
| Scheduled installs do not run | Auto-install is off by default. It must be enabled in the policy, and it only creates jobs while the policy's maintenance window is open |

Cancelling a queued job takes effect immediately. Cancelling a running install job tells the agent
at its next check-in. The agent then stops before the next update; an update that is already
downloading or installing is allowed to finish, and the remaining updates are reported as
cancelled.
