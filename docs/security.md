# EasyPatch Security Model and Threat Model

The EasyPatch server is exposed to the internet and manages software installation on
customer computers. Security is designed around one principle:

> **A compromised server must not become a remote-code-execution channel into customer networks.**

The agent only performs a fixed set of operations: Windows Update Agent (WUA) scan, download
and install, plus inventory collection. It installs only updates that WUA offers from the
endpoint's own configured source (Windows Update, Microsoft Update or the customer's WSUS
server). Those updates are signed by Microsoft and their signatures are checked by Windows.
There is no shell, no script execution and no file push. The agent restarts a computer only
for a restart job that an administrator created, directly or through a restart schedule, and
changes Windows Update policy values only for settings an administrator configured.

## Assets

| Asset | Where it lives | Protection |
|---|---|---|
| Admin credentials | `users.password_hash` | Argon2id (m=19 MiB, t=2, p=1). Login rate limits and account lockout. |
| MFA secrets | `users.mfa_secret_protected` | ASP.NET Data Protection; the keys are in a separate volume |
| Admin sessions | Browser cookie and `user_sessions` | Random 256-bit token, only its SHA-256 is stored. `__Host-` cookie that is `Secure`, `HttpOnly` and `SameSite=Strict`. Idle and absolute expiry. Can be revoked. |
| Agent private keys | Endpoint `C:\ProgramData\EasyPatch\agent.key` | Generated on the device and never transmitted. DPAPI (LocalMachine). Directory ACL allows only SYSTEM and Administrators. |
| Agent public keys | `agent_credentials` | Not secret. Expire and can be revoked. |
| Enrollment tokens | `enrollment_tokens.token_hash` | 256-bit random value shown once. Only the SHA-256 is stored. Expiring, use-limited and revocable. |
| Database | `pgdata` volume | Internal network only, password provided as a Compose secret |

## Admin authentication

* **Passwords.** 12 to 256 characters, at least 4 distinct characters, and the username must
  not appear in the password. There are no composition rules, following NIST SP 800-63B.
* **Rate limits and lockout.** Login, MFA verification and the password routes share a
  limit of 20 requests per minute per IP. Five consecutive failures lock the account,
  starting at 15 minutes and doubling with each lockout, capped at 24 hours. A locked account
  gets the same "invalid username or password" answer as a wrong password, and unknown
  usernames get it too. `unlock` (CLI) clears a lockout.
* **Multi-factor authentication (MFA).** TOTP (RFC 6238, SHA-1, 6 digits, 30 s step, ±1 step
  tolerance). A time step can only be used once.
  * MFA is **required by default** (`RequireMfa` in server settings). Users without MFA can
    only enroll MFA until they do.
  * Users get 10 single-use recovery codes, stored hashed.
* **Sessions.**
  * Server-side and revocable, either individually or all at once.
  * Changing your password revokes all your other sessions.
  * Defaults: 30-minute idle timeout, 12-hour absolute lifetime.
* **CSRF.** Session cookies use `SameSite=Strict`. In addition, every unsafe method must send
  an `X-XSRF-TOKEN` header carrying the session ID protected with ASP.NET Data Protection
  (purpose `EasyPatch.Csrf.v1`). The server unprotects it and compares it with the session of
  the request. The value is delivered in a JavaScript-readable `XSRF-TOKEN` cookie.
* **Bootstrap and recovery.** No first-run web page exists. The first Super Administrator is
  created with `docker compose run --rm api create-admin`. Password resets are either
  administrator-issued one-time links (valid 60 minutes) or the `reset-password` CLI command.
* **Roles.**

  | Role | Permissions |
  |---|---|
  | SuperAdmin | Everything, including users, server settings and the lifecycle table |
  | Admin | Organizations, sites, endpoints, jobs (including restarts and user messages), policies (including Windows Update settings and restart schedules), enrollment |
  | ReadOnly | View only |

  A SuperAdmin can restrict an Admin or ReadOnly user to specific organizations
  (`user_organization_access`; no rows means all organizations). Every admin query goes
  through one tenant-scope service, and anything outside the user's organizations answers
  `404`. Only unrestricted users can create organizations.

## Agent enrollment

There are two ways to enroll an agent.

1. **Enrollment token** (`msiexec … ENROLLMENTTOKEN=…`). A token is valid for one organization
   (and optionally one site). It expires (default 24 h, maximum 30 days) and is limited to a
   number of uses (default 1). Administrators can revoke it. Enrollment is rate-limited to
   30 requests per minute per IP.
2. **Deployment code with approval** (the tokenless PowerShell/RMM install).
   * The deployment code is a non-secret routing identifier. On its own it grants nothing.
   * The agent enrolls with its public key, which creates a **pending request**. The agent
     then polls a signed status endpoint. No endpoint or credential exists until an
     administrator approves the request in the GUI.
   * The administrator sees the hostname, serial number, manufacturer and model, OS, agent
     version, source IP and the **key fingerprint**. On the endpoint, the fingerprint is
     written to the agent log and the Application event log, `EasyPatch.Agent.exe status`
     prints it, and the generated install script displays it, so the RMM output can be
     compared with the approval queue.
   * Limits:
     * Pending requests per code are capped (default 500).
     * Requests expire after 7 days.
     * An optional source CIDR allow-list can restrict where requests come from.
     * The code can be revoked.
   * A leaked code therefore lets an attacker add entries to the approval queue, and nothing
     more.

The MSI marks `ENROLLMENTTOKEN` as a hidden property (`MsiHiddenProperties`), which Windows
Installer masks in its property listings. It also overrides the progress template of
`WriteRegistryValues` so that registry values are not echoed into the log. The MSI stores
bootstrap values in `HKLM\SOFTWARE\EasyPatch\Agent` with an ACL that allows only SYSTEM and
Administrators. The agent deletes the token after it enrolls successfully.

Microsoft does not document masking for every line of a verbose Windows Installer log, and the
`msiexec` command line itself is visible to process auditing and endpoint security tools.
Treat verbose logs and command-line telemetry from token installs as sensitive, or use one of
the alternatives in [agent-installation.md](agent-installation.md#protecting-the-enrollment-token).

## Agent request signing

Every agent request after enrollment carries these headers:

```
X-EasyPatch-Credential:     c_… (or r_… for a pending enrollment request)
X-EasyPatch-Timestamp:      Unix seconds
X-EasyPatch-Nonce:          128-bit random, base64url
X-EasyPatch-Content-SHA256: lowercase hex SHA-256 of the exact body bytes
X-EasyPatch-Signature:      base64 ECDSA P-256 / SHA-256 signature (IEEE P1363 format)
```

The signature covers this canonical string, with lines joined by `\n`:

```
EASYPATCH-ECDSA-P256-SHA256
<HTTP METHOD>
<path and query exactly as sent>
<timestamp>
<nonce>
<content sha256>
<credential id>
```

The server enforces the following:

* The timestamp is within ±300 s. A rejection includes the server time so that an agent with
  a skewed clock can correct its offset.
* Each nonce is accepted once per credential within the timestamp window (in-memory cache).
* The body hash matches.
* The signature verifies against the stored public key.
* The credential is not expired or revoked, and the endpoint is active.

The authenticated principal carries the endpoint ID and the organization ID. Every agent
endpoint scopes its reads and writes to that endpoint ID. Jobs are checked against the
endpoint before they are acknowledged or updated. An agent can never reference another
endpoint or organization.

**Rotation.** Credentials expire after 365 days by default. Check-in responses ask the agent
to rotate once a credential is within 30 days of expiry. The agent creates a new key and
calls `credentials/rotate`, signed with the old key. The old credential stays valid for one
hour so requests already in flight still succeed. Rotation is idempotent per public key.

**Revocation.** An administrator can revoke an endpoint. All its credentials are revoked
immediately, the credential cache is evicted, and the agent stops calling and backs off.
Re-enrolling requires a new token or approval.

**Why signing instead of client certificates.** Mutual TLS behind Caddy would conflict with
its automatic certificate management. Bearer tokens or HMAC keys would require the server to
store reusable secrets. Signing gives per-device asymmetric authentication with replay
protection, and the server holds nothing that would let an attacker impersonate an agent.

## Restarts, user messages and Windows Update settings

These features act on the computer and its users, so each is limited to a fixed, validated
shape and to an explicit administrator decision.

* **Restarts.** A `Restart` job is created only by an Operator (Admin or SuperAdmin) for chosen
  endpoints, or by the worker from a restart schedule that an Operator configured in policy.
  Install jobs never restart a computer. The job carries only a mode, a condition and two
  durations, all range-checked by the server and again by the agent. Manual restarts are
  recorded in the audit log with their parameters; schedule changes are recorded as policy
  changes. A restart job that is not picked up within its expiry period (at most 24 hours) is
  abandoned rather than run late.
* **User messages.** The only messages are the presets listed by `GET /api/v1/user-messages`.
  There is no free text, so a compromised account or server cannot show users arbitrary
  content (for example a phishing message that appears to come from IT). The only variable
  part is the restart time in the *Restart scheduled* message.
* **Windows Update settings.** Every group is *Not configured* by default, so nothing changes
  until an administrator configures it. The agent writes only the registry values listed in
  [architecture.md](architecture.md#windows-update-settings), under
  `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate`. It never writes the WSUS
  values (`WUServer`, `WUStatusServer`, `UseWUServer`) and cannot redirect an endpoint to a
  different update server. It keeps the original values and restores them when the setting is
  removed, when the agent is uninstalled and when its credential is revoked. When another
  policy source changes a value, the agent reports a conflict and stops re-applying, instead
  of overriding domain Group Policy in a loop.

### Tray and local pipe

The tray runs as the signed-in user, unprivileged. It talks to the service (LocalSystem) over
the named pipe `\\.\pipe\EasyPatchAgent`:

* **Access.** The pipe ACL grants full control to SYSTEM and Administrators and read/write to
  interactive users, and denies network logons, so remote clients cannot connect. The service
  creates the pipe with `FirstPipeInstance`, so another process cannot create it first.
* **Identity.** The service takes the client's session from Windows
  (`GetNamedPipeClientSessionId`), never from the message, and refuses session 0 (services).
  The tray checks that the pipe server runs in session 0 before trusting it.
* **What a tray can do.** Exactly two things: answer the restart request that is currently
  shown, with *Restart now* or one of the offered postponements. Anything else is ignored,
  and a malformed or oversized frame (over 16 KB) closes the connection. A local user can
  therefore at most restart the computer sooner or postpone a restart within the
  administrator's deadline, which the restart prompt allows anyway. No input from the tray is
  ever executed, and the tray cannot change the agent's configuration, jobs or credentials.

## Threat model (STRIDE summary)

| # | Threat | Mitigation |
|---|---|---|
| S1 | Attacker impersonates an agent | Per-device ECDSA keys, signed requests, nonce and timestamp checks, revocation |
| S2 | Attacker impersonates the server to an agent | The agent uses normal Windows TLS validation of the server certificate (the public CA from Caddy's ACME). TLS validation cannot be turned off. |
| S3 | Admin credential stuffing or brute force | Argon2id, per-IP rate limits, account lockout, mandatory TOTP |
| T1 | A forged job or tampered payload is sent to an agent | TLS, plus the agent only accepts the job types it knows. Install payloads can only select WUA updates by GUID or category, never arbitrary binaries. Restart payloads are a mode and range-checked durations; message payloads are a preset name. |
| T2 | An agent submits false data for another tenant | The endpoint ID comes from the authenticated credential, never from the request body. Every write is scoped to it. |
| T3 | SQL injection | EF Core parameterized queries. The few raw SQL statements use parameters only. Sort fields come from an allow-list. |
| R1 | Admin actions are denied after the fact | `audit_logs` records the user, IP, correlation ID and details of every mutating admin action |
| I1 | Tenant data leaks between organizations | One tenant-scope service, organization-filtered queries and integration tests for isolation |
| I2 | Secrets appear in logs | The server and agent never log tokens or keys. The MSI token property is hidden and registry values are not echoed to MSI logs (see above for limits). Structured logs leave out request bodies. |
| I3 | Database exposure | No published port, internal Docker network, Compose secret for the password |
| D1 | Check-in floods | Per-credential and per-IP rate limits, request size limits, jitter |
| D2 | Enrollment queue spam | A deployment code is required, pending requests are capped and expire, per-IP rate limit |
| E1 | Server compromise used to execute code on endpoints | The agent has no generic execution capability, and update sources are controlled by Windows. Agent self-update is **not implemented**. When it is, it must verify Authenticode against a publisher identity pinned in the agent, not a hash supplied by the server. |
| E2 | A local non-admin user reads agent secrets | ProgramData ACL (SYSTEM and Administrators), DPAPI, registry ACL on bootstrap values |
| E3 | Cross-site request forgery or XSS in the admin UI | SameSite=Strict, CSRF header check, a strict CSP from Caddy, and React escaping |
| E4 | A local user abuses the tray pipe to make the service do something | The pipe accepts only *restart now* or an offered postponement for the current restart request, from interactive sessions; the session comes from Windows. See [Tray and local pipe](#tray-and-local-pipe). |
| D3 | A compromised admin account or server restarts many computers or weakens Windows Update settings | Requires the Operator role and MFA; every manual restart and policy change is audited. Restarts warn signed-in users according to the chosen mode. Windows Update changes are limited to the listed values (never the WSUS server), are shown per endpoint in the console, and are restored when removed. This is a disruption risk, not code execution. |

## Server hardening checklist

* TLS 1.2 minimum (Caddy default; TLS 1.3 is preferred automatically). HSTS is enabled.
* Security headers: CSP, `X-Content-Type-Options`, `X-Frame-Options: DENY`,
  `Referrer-Policy`, `Permissions-Policy`.
* CORS: none. The SPA is served from the same origin, so no cross-origin access is allowed.
* The .NET containers run as a non-root user with a read-only root filesystem, all
  capabilities dropped and `no-new-privileges`. Caddy keeps only `NET_BIND_SERVICE`. All
  images are pinned to exact versions.
* PostgreSQL has no published port and sits on an internal Docker network without egress.
* OpenAPI and Swagger UI are only available to signed-in users. Swagger UI is the one page
  served with a relaxed CSP (it needs inline scripts); the console itself has no inline
  scripts or styles.
* Dependency scanning: `scripts/audit-dependencies.sh` runs `dotnet list package --vulnerable`
  and `npm audit`.

## Known limitations

* The nonce replay cache is in memory, so the documented deployment runs a single API
  replica. Running several replicas would need a shared nonce store.
* Consumer Windows 10 ESU enrollment (through a Microsoft account) cannot be detected
  reliably. Volume-license ESU keys are detected through licensing WMI. Administrators can
  set ESU status manually.
* An enrollment token passed to `msiexec` appears on the command line, which process auditing
  and endpoint security tools may record. Tokens are therefore limited-use, short-lived and
  revocable, and deployment codes or the environment-variable method avoid the problem.
* The server cannot verify the Authenticode signatures of agent MSIs it hosts. The generated
  PowerShell installer verifies the SHA-256 and, optionally, the Authenticode signature on
  the endpoint.
* The Data Protection key ring (`dpkeys` volume) is stored unencrypted on the Docker host, as
  is the database volume. Anyone with root on the host can read MFA secrets. Protect the
  host, and encrypt backups (`scripts/backup.sh` output includes the key ring).
