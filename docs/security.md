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

1. **Enrollment token** (`msiexec … ENROLLMENTTOKEN=…`, or `easypatch-agent enroll --token-file`
   on Linux). A token is valid for one organization
   (and optionally one site). It expires (default 24 h, maximum 30 days) and is limited to a
   number of uses (default 1). Administrators can revoke it. Enrollment is rate-limited to
   30 requests per minute per IP.
2. **Deployment code with approval** (the tokenless PowerShell/RMM install, or the Linux install
   script).
   * The deployment code is a non-secret routing identifier. On its own it grants nothing.
   * The agent enrolls with its public key, which creates a **pending request**. The agent
     then polls a signed status endpoint. No endpoint or credential exists until an
     administrator approves the request in the GUI.
   * The administrator sees the hostname, serial number, manufacturer and model, OS, agent
     version, source IP and the **key fingerprint**. On the endpoint, the fingerprint is
     written to the agent log and the Application event log (the journal on Linux),
     `EasyPatch.Agent.exe status` and `easypatch-agent status` print it, and the generated
     install scripts display it, so the RMM output can be compared with the approval queue.
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

### Install scripts and private CAs

The Windows install script normally downloads over TLS with the computer's certificate
validation, and its trust rests on that connection: the script carries the MSI's SHA-256.
*Skip certificate check* (off by default, chosen per generated script) is for servers whose
certificate the endpoint does not trust, such as the lab setup with Caddy's internal CA:

* The one-liner, the script, the CA certificate and the MSI are downloaded **without
  certificate validation**. Someone who can intercept the connection during the installation can
  hand the computer a different script, which then runs as Administrator or SYSTEM. This is the
  same trust as a first visit to a site whose certificate warning you click through, so the
  option is for networks you trust. The MSI hash is still checked, but against the script that
  came over the same connection.
* The script reads the CA certificate from `/server-ca.crt` (published by `Caddyfile.dev` only;
  Caddy serves exactly `root.crt`, never the CA key) and passes it to the MSI as `SERVERCACERT`.
  The agent stores it with its other bootstrap values (`HKLM\SOFTWARE\EasyPatch\Agent`,
  SYSTEM and Administrators only) and adds it as a trust root **for its own connection to the
  server only**. Other programs on the computer are not affected, unlike importing the CA into
  the machine's Trusted Root store, which would also let the CA's key impersonate any website.
* After the installation, every connection is validated again. Certificates are accepted when
  Windows trusts them, or when they chain to the recorded CA, are valid now, are meant for
  server authentication and match the server name. Revocation is not checked for the recorded
  CA (Caddy's internal CA publishes no revocation information). The window in which an
  attacker on the network could interfere is therefore the installation only, not the life of
  the agent.
* Turning certificate validation off in the agent itself is not possible. It would let anyone
  on the network impersonate the server to the agent for as long as it runs.

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
* **What a pending restart is for.** Reading it is read-only (registry). While a restart is
  pending, Windows agents 1.5.3 and later send the update IDs, servicing package names and file
  paths Windows has queued for it. File paths can name programs a user installed; the agent
  replaces the user name in profile paths with `*` (`C:\Users\*\…`), sends at most 25 paths of at
  most 260 characters, and the server caps and cleans the list again before storing it. The
  details are cleared when the restart is done.
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
* **Uninstalling the agent.** An `UninstallAgent` job is created only by an Operator for one
  endpoint, after typing the computer name to confirm, and is recorded in the audit log. It
  has no payload: the agent can only remove its own package (`msiexec /x` of the product with
  EasyPatch's fixed upgrade code, or `apt-get remove`/`rpm -e easypatch-agent`), never another
  program, and the server cannot pass it a command or arguments. The package's own uninstall
  step unenrolls the agent; the server marks the job completed only when that authenticated
  unenrollment arrives, not when the job is sent.

### Tray and local pipe

The tray runs as the signed-in user, unprivileged. It talks to the service (LocalSystem) over
the named pipe `\\.\pipe\EasyPatchAgent`:

* **Access.** The pipe ACL grants full control to SYSTEM and Administrators and read/write to
  interactive users, and denies network logons, so remote clients cannot connect. The service
  creates the pipe with `FirstPipeInstance`, so another process cannot create it first.
* **Identity.** The service takes the client's session from Windows
  (`GetNamedPipeClientSessionId`), never from the message, and refuses session 0 (services).
  The tray checks that the pipe server runs in session 0 before trusting it.
* **What a tray can do.** Answer the restart request that is currently shown, with *Restart
  now* or one of the offered postponements; and, from the status window (agent 1.5.0 and
  later), ask for a check-in, ask for an update check, and read the recent agent log. The
  three status-window requests carry no parameters. A check-in is the agent's regular
  heartbeat and an update check is its regular scan: neither installs, removes or restarts
  anything. They are limited for the whole computer (a check-in every 30 seconds, an update
  check every 5 minutes and never while Windows Update is busy), so any number of signed-in
  users cannot make the agent do more than that. Each request is logged with the session it
  came from. Anything else is ignored, and a malformed frame, or one over 16 KB from a tray,
  closes the connection. A local user can therefore at most restart the computer sooner,
  postpone a restart within the administrator's deadline (which the restart prompt allows
  anyway), or make the agent check in or scan sooner. No input from the tray is ever executed,
  and the tray cannot change the agent's configuration, jobs or credentials.
* **What the log shows.** Every signed-in user can read the newest 200 log entries in the
  status window. Only the first line of each information, warning or error entry is sent, so
  exception details and debug output stay in the file. The log never contains tokens, keys or
  passwords, and every line passes through the redaction filter again before it is sent. It
  does contain operational details such as update titles, job and endpoint IDs, the credential
  ID (not a secret: every request is signed with the private key) and the server address. The
  log files themselves stay readable only by SYSTEM and Administrators. To keep users from
  opening the status window, hide the icon in policy.

## Linux agent

The Linux agent follows the same model with these platform specifics
([linux-agent.md](linux-agent.md)):

* **It runs as root**, which apt, dnf and `shutdown` require, with `UMask=0077`. Its settings
  (`/etc/easypatch`, 0700, `agent.conf` 0600) and data (`/var/lib/easypatch`, 0700) are
  readable by root only.
* **Private key.** Encrypted with `systemd-creds` and the host key
  (`/var/lib/systemd/credential.secret`) on systemd 250 and later; otherwise protected by file
  permissions only. The console shows which one each endpoint uses. Root can read the key either
  way; the host key protects copies of `/var/lib/easypatch` such as backups.
* **Enrollment token.** Never accepted as a command-line argument, because arguments are
  visible to every local user. `enroll` reads it from a file, standard input or
  `EASYPATCH_ENROLLMENT_TOKEN`, and the token is removed from `agent.conf` after enrollment.
  The command the console shows pipes the token in with the `printf` builtin, which keeps it out
  of the process list but not out of the shell history.
* **The install script** contains the server URL and the deployment code, nothing secret. It
  downloads the package from the server over HTTPS and checks the SHA-256 that the server put in
  the script, so its trust rests on the TLS connection to the server, like the PowerShell
  script.
* **Package installation.** The server sends update GUIDs; the agent installs only upgrades that
  its own scan found in the repositories the computer is configured for, at the exact versions
  it found. Package names, architectures and versions are validated, passed to apt or dnf as
  separate arguments after `--` (no shell), and no options come from the server. The server
  cannot name a package, a version or a repository.
* **Output.** Package manager output sent to the server (error summaries) has credentials in
  repository URLs (`user:password@`, `token=` and similar query parameters) replaced with `***`.

## Agent release signing

Windows agents 1.2.0 and newer and all Linux agents can update themselves. The EasyPatch server is not trusted to decide
what code runs on endpoints: it only relays agent packages and their signatures, and the agent
checks them against a key that was built into it.

**The release key.** An ECDSA P-256 key pair, created once with
`dotnet run --project tools/EasyPatch.ReleaseSigner -- generate-key`. The private key is never
copied to the EasyPatch server or into source control. The public key
(`easypatch-release-key.pub.pem`) is built into the agent executable by the build scripts.
An agent built without it reports that it cannot update itself and must be upgraded by
installing a newer package.

* **Official releases** (the packages attached to GitHub Releases) are built and signed by the
  `release` GitHub Actions workflow when a version tag is pushed. The private key is stored as an
  encrypted secret of the `release` environment, which only workflow runs for `v*` tags can
  use. The job writes it to the runner's temporary directory, signs, verifies the signatures
  against the committed public key [`keys/easypatch-release-key.pub.pem`](../keys/easypatch-release-key.pub.pem)
  and deletes the file. Agents from official releases accept only packages signed with this key.
* **Your own builds.** The build scripts read the key from `~/.easypatch/easypatch-release-key.pem`
  (mode 600) or `EASYPATCH_RELEASE_KEY`, and the public key from
  `~/.easypatch/easypatch-release-key.pub.pem` or `EASYPATCH_RELEASE_PUBLIC_KEY`. Set
  `EASYPATCH_RELEASE_KEY_PASSWORD` to store the private key encrypted (PKCS#8, AES-256-CBC,
  PBKDF2-SHA256 with 600,000 iterations). Agents built with your own key accept only packages
  signed with your key, so they do not update to official releases, and official agents do not
  update to your builds.

**What is signed.** The build scripts write `<package>.sig` next to each MSI, `.deb` and `.rpm`
package. It signs the file name, version, architecture, size and SHA-256 of the package, prefixed with a fixed domain
string, so a signature cannot be reused for another file, version or architecture. The
server reads the `.sig` file only to offer the package; it does not and cannot vouch for it.

**What the agent checks**, in this order, before anything is installed:

1. The signature's key ID is one of the keys built into the agent, and the signature verifies.
   The server cannot supply or replace keys.
2. The package is newer than the running agent (agents are never downgraded) and is for the
   agent's architecture and package format (MSI, `.deb` or `.rpm`).
3. The download, from `downloads/agent/<file>` on the agent's own server over TLS, has exactly
   the signed size and SHA-256. The agent checks the hash again immediately before starting
   the installer, holding the file open (on Windows this prevents changes in between; on Linux
   the file is in a root-only directory).
4. It runs `%SystemRoot%\System32\msiexec.exe /i <package> /qn /norestart /l*v <log>` and
   nothing else. No MSI properties are passed, so the installed server URL is kept and nothing
   is re-enrolled. `/norestart` means the computer is never restarted by an agent update.
   On Linux it runs `apt-get install … <package>` or `dnf --disablerepo=* install <package>`
   in the transient unit `easypatch-agent-update`, with no other arguments from the server.
   The package keeps `/etc/easypatch` and `/var/lib/easypatch`, so nothing is re-enrolled.

The job completes only when the new version is running and reports it. If the installer
fails, the previous version reports the failure with the installer's exit code.

**Who can start an update.** An Operator (Admin or SuperAdmin) with *Update agent*, or the
worker when the *Agent updates* policy is *Automatic* (the default). With *Automatic*, copying a
newer signed package into the agent-packages directory starts the rollout to every computer whose
policy allows it, at most `EasyPatch:AutomaticAgentUpdateConcurrency` (default 25) at a time.
Set the policy to *Manual* where updates must be scheduled by hand.

**Changing the key.** The agent accepts several `PUBLIC KEY` blocks in its public key file. To
move to a new key, append the new public key to the file, release a version signed with the old
key (it now trusts both), and sign later releases with the new key. If the private key is lost,
agents cannot update themselves until a version with a new key is installed manually. If it is
stolen, treat it like a stolen code-signing certificate: build agents with a new key only and
install them manually, because agents that still trust the old key would accept a package signed
with it from a compromised server.

Release signatures are separate from Authenticode. They work without a code-signing
certificate; when Authenticode signing is configured, it is applied first, and the release
signature covers the signed file.

## Notifications and webhooks

Alert notifications and the vulnerability data downloads (see [Vulnerability data](#vulnerability-data))
are the only features that make the server open outbound connections (apart from ACME in Caddy).
Notifications are configured by super administrators only, under
*Settings → Alert rules* and *Settings → Notifications*, and every change is audited
(`notifications.*`, `alert_rule.*`, `alert.acknowledge`) without the secrets.

* **Secrets at rest.** The SMTP password, webhook URLs (Slack and Teams URLs contain their own
  access token) and webhook signing keys are encrypted with ASP.NET Data Protection, with keys
  on the `dpkeys` volume. The API never returns them. The SMTP password is write-only, a webhook
  URL is shown only as its host name, and a signing key is shown once, when it is created or
  rotated. The keys on `dpkeys` are not encrypted themselves: protect the volume and its backups
  like the database. If the keys are lost, enter the secrets again.
* **Only the worker connects out.** The `worker` container is the only one on the `egress`
  network. It mounts `dpkeys` read-only. The API and PostgreSQL stay without egress.
* **Webhook egress guard (SSRF).**
  * A webhook URL must be `https://`, without user name or password, at most 2048 characters.
  * Requests do not follow redirects (a 3xx counts as a failure), use no proxy and send no cookies.
  * The handler resolves the host name itself and checks **every address it connects to**, so a
    DNS answer that changes between validation and connection (DNS rebinding) does not get
    through.
  * Always refused: loopback, link-local (including cloud metadata at 169.254.169.254),
    multicast, reserved and documentation ranges, unspecified addresses, and the server's own
    interface networks. IPv4-mapped, NAT64 and 6to4 IPv6 addresses are checked as the IPv4
    address they contain.
  * Private ranges (10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7) are refused unless a
    super administrator turns on *Allow webhooks to private network addresses* (for an
    on-premises PSA or chat server).
* **Logs and errors.** The webhook HTTP client has request logging turned off, so URLs do not
  reach the logs. Delivery errors are reduced to a message without URLs, tokens or passwords
  (for example `hooks.example.com answered HTTP 403`), which is what the delivery log and the
  worker log show.
* **Signed generic webhooks.** Generic webhooks carry
  `X-EasyPatch-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256(key, "<t>.<body>")>`. Receivers
  should compare the HMAC in constant time and reject old timestamps (for example older than
  five minutes) to stop replays.
* **What a notification contains.** Host names, organization names, alert titles and details,
  and links to the console. No credentials or update payloads. Email and chat services see this
  content; choose channels accordingly.
* **Email transport.** STARTTLS or TLS-on-connect with the system's certificate validation.
  *None* sends the password and messages unencrypted and is meant only for a relay on a trusted
  network. A super administrator can turn on **Skip certificate validation** for an SMTP server
  whose certificate the containers do not trust (for example a self-signed certificate on an
  internal relay). It is off by default, recorded in the audit log and shown with a warning. The
  connection stays encrypted, but anyone able to intercept or redirect it could impersonate the
  server and read the SMTP password, alerts and monthly reports. A trusted certificate on the
  mail server is the better fix.

## Vulnerability data

The worker downloads public vulnerability data to show which CVEs updates fix and to flag
known-exploited ones ([compliance.md](compliance.md#vulnerabilities-and-known-exploited-updates)):

* **Fixed hosts, HTTPS only, nothing sent.** The URLs are built in: `www.cisa.gov` (with
  `raw.githubusercontent.com/cisagov/kev-data` as fallback), `api.msrc.microsoft.com` and
  `security-tracker.debian.org`. Requests are plain GETs without credentials or any data about
  endpoints. Redirects are not followed and the system's certificate validation applies (it
  cannot be turned off). Super administrators can turn the downloads off under *Settings →
  Server → Vulnerability data*.
* **Untrusted input.** The documents are parsed as data only, with size limits (32 MB for the
  KEV catalog and the MSRC list, 128 MB per MSRC document, 512 MB for the Debian tracker, which is
  streamed rather than loaded). MSRC document IDs from the list must look like `2026-Nov` before
  they are put in a URL. CVE IDs must match `CVE-yyyy-nnnn…`; other values are dropped. Feed data
  is stored in its own tables and never turned into commands: it changes which updates are shown
  first and, by default, lets known-exploited updates skip a rollout delay. It cannot add updates,
  install anything or restart a computer.
* **Failures.** A failed download keeps the previous data and is retried after an hour. The stored
  and logged error names the host and the HTTP status only.

The CVEs Linux agents report come from the computer's own package manager (dnf advisories) and
are validated the same way.

## Monthly reports

Monthly report PDFs ([compliance.md](compliance.md#monthly-reports)) describe a customer's
exposure: hostnames, missing updates and known-exploited vulnerabilities.

* **Opt-in recipients.** Reports are emailed only to addresses entered for the organization's
  schedule or when sending a report; there is no default recipient. Changing a schedule, generating,
  sending and deleting a report need the Operator role and are audited (the audit entry records the
  number of recipients, not the addresses).
* **Encrypted transport only.** Reports are emailed only when the SMTP security setting is STARTTLS
  or TLS. The console refuses to queue them and the worker refuses to send them over a plain
  connection. With *Skip certificate validation* turned on they are still encrypted, but the mail
  server's identity is not verified (see [Notifications and webhooks](#notifications-and-webhooks)).
  The SMTP password is handled as for notifications (encrypted at rest, never returned).
* **Tenant isolation.** Listing, downloading, sending and deleting reports, and reading or changing
  schedules, are limited to the user's organizations.
* **Storage.** The PDFs are stored in the database (and therefore in backups) and deleted after the
  report retention period. Generating a report makes no outbound connection: the font is embedded
  in the server.

## Application updates (winget)

Application updates ([application-updates.md](application-updates.md)) are off by default. When
an administrator turns them on and approves applications, the Windows agent runs winget as
LocalSystem.

* **No general command execution.** The agent builds two fixed command lines (`winget list` and
  `winget upgrade`) and passes them as an argument vector, never through a shell. The only
  variable parts are a package ID and a version, both checked against strict patterns (letters,
  digits and `.-_+`; IDs must contain a dot) on the server and on the agent. `--force`,
  `--ignore-security-hash`, installer overrides and sources other than the winget community
  repository are never used.
* **Upgrades of installed applications only.** The agent checks only approved IDs and offers
  only upgrades of applications that winget lists as installed. The version installed is the one
  the agent's own check found; the server cannot choose it, downgrade an application or install a
  new one.
* **Approval enforced on both sides.** The server marks only approved application updates as
  required and puts the approved list in every install job; the agent's install filter skips
  application updates that are not on it. Changing the list needs the Operator role (global
  policy: SuperAdmin) and is audited like every policy change.
* **Verified results.** An upgrade is reported as installed only when winget lists the new version
  afterwards, never on the exit code alone.
* **Validated reports.** The server ignores application updates whose ID or version is malformed
  or whose update GUID does not match the ID and version.
* **Residual risk.** The packages come from the winget community repository and the vendors'
  download sites; winget checks each installer's SHA-256 against the reviewed manifest. A
  compromised manifest or vendor installer would run as LocalSystem on the computers where that
  application is approved and installed, as it would for any software deployment. A third-party
  installer can restart the computer by itself despite silent mode. Approve only well-known
  applications.

## Threat model (STRIDE summary)

| # | Threat | Mitigation |
|---|---|---|
| S1 | Attacker impersonates an agent | Per-device ECDSA keys, signed requests, nonce and timestamp checks, revocation |
| S2 | Attacker impersonates the server to an agent | The agent uses the operating system's normal TLS validation of the server certificate (the Windows certificate store, or the system CA store on Linux; the public CA from Caddy's ACME). TLS validation cannot be turned off. A Windows agent installed with *Skip certificate check* also accepts certificates from the one CA recorded at installation (`SERVERCACERT`), for its EasyPatch connection only, still with name and validity checks. See [Install scripts and private CAs](#install-scripts-and-private-cas). |
| S3 | Admin credential stuffing or brute force | Argon2id, per-IP rate limits, account lockout, mandatory TOTP |
| T1 | A forged job or tampered payload is sent to an agent | TLS, plus the agent only accepts the job types it knows. Install payloads can only select, by GUID or category, Windows updates, Linux package upgrades or upgrades of approved, installed applications (winget) that the agent's own scan found, never arbitrary binaries or packages. Restart payloads are a mode and range-checked durations; message payloads are a preset name. Agent update payloads name a package and its release signature, which the agent verifies itself. Uninstall jobs have no payload and remove only the agent's own package. |
| T2 | An agent submits false data for another tenant | The endpoint ID comes from the authenticated credential, never from the request body. Every write is scoped to it. |
| T3 | SQL injection | EF Core parameterized queries. The few raw SQL statements use parameters only. Sort fields come from an allow-list. |
| T4 | A tampered or malicious vulnerability feed | Fixed HTTPS hosts with normal certificate validation, no redirects, size limits, strict parsing (document IDs and CVE IDs are validated). Feed data only changes display order, CVE lists and, by default, whether a known-exploited update skips the rollout delay: it cannot add updates, run commands or restart computers. Super administrators can turn the downloads off. See [Vulnerability data](#vulnerability-data). |
| R1 | Admin actions are denied after the fact | `audit_logs` records the user, IP, correlation ID and details of every mutating admin action |
| I1 | Tenant data leaks between organizations | One tenant-scope service, organization-filtered queries and integration tests for isolation |
| I2 | Secrets appear in logs | The server and agent never log tokens or keys. The MSI token property is hidden and registry values are not echoed to MSI logs (see above for limits). Structured logs leave out request bodies. |
| I3 | Database exposure | No published port, internal Docker network, Compose secret for the password |
| I4 | A notification channel is used to reach internal services (SSRF) or leak secrets | Super administrator only; https only; no redirects or proxy; every connected address is checked against blocked ranges (private ranges only when explicitly allowed); secrets encrypted at rest and never returned; sanitized errors. See [Notifications and webhooks](#notifications-and-webhooks). |
| I5 | A monthly report reaches the wrong people or is read in transit | Recipients are entered per organization (no default), changes are audited, Operator role required; email only over STARTTLS or TLS (server certificate validated unless a super administrator turns that off); tenant-scoped API. See [Monthly reports](#monthly-reports). |
| D1 | Check-in floods | Per-credential and per-IP rate limits, request size limits, jitter |
| D2 | Enrollment queue spam | A deployment code is required, pending requests are capped and expire, per-IP rate limit |
| E1 | Server compromise used to execute code on endpoints | The agent has no generic execution capability. Windows updates come from the sources Windows is configured for, and Linux packages from the repositories the computer is configured for. Application updates (off by default) can only upgrade an installed application to the version the winget community repository offers; a compromised server could approve more applications, but not choose versions, sources or installers. See [Application updates](#application-updates-winget). Agent updates install only packages whose release signature verifies against a public key built into the agent; the server relays packages and signatures but cannot supply keys, sign packages or downgrade an agent. See [Agent release signing](#agent-release-signing). |
| E2 | A local non-admin user reads agent secrets | Windows: ProgramData ACL (SYSTEM and Administrators), DPAPI, registry ACL on bootstrap values. Linux: root-only `/etc/easypatch` and `/var/lib/easypatch`, `systemd-creds` where available, no token on the command line |
| E3 | Cross-site request forgery or XSS in the admin UI | SameSite=Strict, CSRF header check, a strict CSP from Caddy, and React escaping |
| E4 | A local user abuses the tray pipe to make the service do something | The pipe accepts only *restart now* or an offered postponement for the current restart request, and the parameterless status-window requests (check in, check for updates, read the recent log), from interactive sessions; the session comes from Windows. Check-ins and update checks are rate-limited for the whole computer and never install or restart anything. See [Tray and local pipe](#tray-and-local-pipe). |
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
  the endpoint. Agent updates do not depend on Authenticode: the agent verifies the release
  signature (see [Agent release signing](#agent-release-signing)).
* A compromised server can still choose *which* signed release an agent updates to, as long as
  it is newer than the installed one, and *when*. It cannot install anything unsigned or older.
  Remove packages of releases you no longer want installed from the agent-packages directory.
* Agents older than 1.2.0 have no release key and cannot update themselves. Install 1.2.0 or
  later on them once.
* The Linux `.deb` and `.rpm` packages are not GPG-signed. Their authenticity is checked by the
  release signature (self-update) or by the SHA-256 in the install script. Systems that require
  GPG signatures for local packages (dnf `localpkg_gpgcheck=1`) cannot install them.
* On Linux, root can read the agent's private key even when `systemd-creds` protects it, and on
  systemd older than 250 (Ubuntu 22.04, RHEL 8) the key is protected by file permissions only.
  A TPM is not used.
* The Data Protection key ring (`dpkeys` volume) is stored unencrypted on the Docker host, as
  is the database volume. Anyone with root on the host can read MFA secrets. Protect the
  host, and encrypt backups (`scripts/backup.sh` output includes the key ring).
