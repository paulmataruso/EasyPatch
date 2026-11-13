# EasyPatch agent: installation and operation

The EasyPatch agent is a Windows service (`EasyPatchAgent`) that reports Windows Update state to
your EasyPatch server and installs the updates an administrator approves. It uses the Windows
Update Agent API that Windows itself uses. The agent:

* **never restarts the computer**. It reports when a restart is pending and why;
* **never changes Windows Update configuration**. WSUS, Group Policy, Windows Update for
  Business and Intune settings are honored and only reported;
* cannot run commands. It executes three job types: update scan, inventory refresh and
  install updates.

## Requirements

| | |
|---|---|
| Operating system | Windows 10, Windows 11, Windows Server 2016 or later, 64-bit (x64 or ARM64) |
| Privileges | Installation and the `enroll`/`unenroll` commands need an administrator; the service runs as LocalSystem |
| Network | Outbound HTTPS (TCP 443 by default) from the endpoint to the EasyPatch server. No inbound ports |
| TLS | The server certificate must be trusted by the Windows certificate store of the computer. Certificate validation cannot be disabled |
| .NET | Not required: the agent is self-contained |

## Choosing an enrollment method

| Method | Use it for | Secret? | Approval |
|---|---|---|---|
| **Enrollment token** (`ENROLLMENTTOKEN`) | Hands-on installs, GPO/Intune deployments where the token is protected | Yes: limited-use, expiring | No, the agent is active immediately |
| **Deployment code** (`DEPLOYMENTCODE`) | RMM scripts and other tokenless mass deployment | No | Yes: each computer appears under *Pending enrollments* until an administrator approves it |

Both are created in the EasyPatch console. The console can also generate the complete
`msiexec` command (tokens) or a PowerShell install script (deployment codes).

## Installing with msiexec

### Enrollment token

```powershell
msiexec /i EasyPatchAgent-1.0.0-x64.msi /qn /norestart `
  SERVERURL=https://patch.example.com `
  ENROLLMENTTOKEN=ept_xxxxxxxxxxxxxxxxxxxxxxxx
```

### Deployment code (tokenless, requires approval)

```powershell
msiexec /i EasyPatchAgent-1.0.0-x64.msi /qn /norestart `
  SERVERURL=https://patch.example.com `
  DEPLOYMENTCODE=EP-7Q4K-PZ2M
```

The agent submits an enrollment request with its public key and polls until an administrator
approves or rejects it. Before approving, compare the **key fingerprint** shown in the console
with the one on the computer. The agent prints it on the computer in three places:

* the output of `EasyPatch.Agent.exe status`;
* event 1001 in the Application event log (source *EasyPatch Agent*);
* `C:\ProgramData\EasyPatch\logs\agent.log`.

### MSI properties

| Property | Required | Description |
|---|---|---|
| `SERVERURL` | First install | `https://` URL of the EasyPatch server, for example `https://patch.example.com` or `https://example.com/easypatch`. Remembered on upgrade |
| `ENROLLMENTTOKEN` | One of the two | Enrollment token. Hidden MSI property (not written to MSI logs). Deleted by the agent once it has been used |
| `DEPLOYMENTCODE` | One of the two | Deployment code. Deleted by the agent once the request is approved or rejected |
| `PROXYURL` | No | `http://proxy:8080` style URL of an outbound proxy. Remembered on upgrade. Authenticating proxies are contacted with the computer account (Negotiate/NTLM) |

Without `PROXYURL` the agent uses the `HTTPS_PROXY`/`HTTP_PROXY` environment variables or the
service account's WinINET proxy settings. WinHTTP proxy settings (`netsh winhttp`) are **not**
used, so set `PROXYURL` on networks that require a proxy.

The installer refuses to run without `SERVERURL` on a first installation, with a `SERVERURL`
that does not start with `https://`, or with both `ENROLLMENTTOKEN` and `DEPLOYMENTCODE`.
`msiexec` exit code `1603` with launch-condition text in the log indicates one of these.

### Protecting the enrollment token

Values on an `msiexec` command line are visible to other administrators through the process
list while the installer runs, and in the command history of the tool that ran it. Tokens are
therefore single-use and expire (24 hours by default). For extra protection, install with the
server URL only and enroll with the token in an environment variable:

```powershell
msiexec /i EasyPatchAgent-1.0.0-x64.msi /qn /norestart SERVERURL=https://patch.example.com
$env:EASYPATCH_ENROLLMENT_TOKEN = '<token from your secret store>'
& "$env:ProgramFiles\EasyPatch Agent\EasyPatch.Agent.exe" enroll --server https://patch.example.com
Remove-Item Env:\EASYPATCH_ENROLLMENT_TOKEN
```

## What the installer creates

| Item | Location |
|---|---|
| Program | `C:\Program Files\EasyPatch Agent\EasyPatch.Agent.exe` (signed when the build is signed) |
| Service | `EasyPatchAgent` ("EasyPatch Agent"), LocalSystem, automatic (delayed) start, restarts after a failure (60 s) |
| Bootstrap settings | `HKLM\SOFTWARE\EasyPatch\Agent`: `ServerUrl`, `ProxyUrl`, and temporarily `EnrollmentToken` / `DeploymentCode`. ACL: SYSTEM and Administrators only |
| Data | `C:\ProgramData\EasyPatch`. ACL: SYSTEM and Administrators only, inheritance disabled |
| Event log source | *EasyPatch Agent* in the Application log |

The service re-applies both ACLs every time it starts and refuses to start if it cannot.

Data directory layout:

```
C:\ProgramData\EasyPatch\
  agent.key                 ECDSA P-256 private key, DPAPI-protected (LocalMachine scope)
  state\agent-state.json    enrollment state, held jobs, timestamps (written atomically)
  outbox\                   reports waiting for delivery (survive restarts and outages)
  logs\agent.log            service log (10 MB x 10 files)
  logs\cli.log              log of enroll/unenroll commands
  agent.lock                prevents two agent processes from running at once
```

The private key never leaves the computer and is never logged. Only its public key is sent to
the server.

## Command line

Run from an elevated prompt:

```text
EasyPatch.Agent.exe status [--json]
EasyPatch.Agent.exe enroll --server <url> (--token <token> | --deployment-code <code>) [--proxy <url>] [--force] [--no-start]
EasyPatch.Agent.exe unenroll [--reason <text>]
EasyPatch.Agent.exe version
```

* `status` shows the service state, enrollment status, server, endpoint ID, credential, key
  fingerprint, last check-in, scan and inventory times, held jobs and queued reports. `--json`
  prints the same as JSON for RMM monitoring.
* `enroll` stops the service, writes the bootstrap settings, enrolls immediately (printing the
  endpoint ID or the fingerprint of the pending request), then starts the service again.
  * Running it again on a computer that is already enrolled with the same server does nothing
    and exits 0, so RMM scripts can run it repeatedly.
  * `--force` notifies the old server (best effort), deletes the current identity and enrolls
    again. It is required to move a computer to a different server.
  * The token can come from `EASYPATCH_ENROLLMENT_TOKEN` instead of `--token`.
* `unenroll` notifies the server (best effort, 30-second limit), deletes the key, state,
  queued reports and bootstrap settings, and leaves the service stopped. The MSI runs it
  automatically on uninstall.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Success (for `enroll`: active, waiting for approval, or already enrolled) |
| 1 | Failure (see `logs\cli.log`) |
| 2 | Invalid arguments or settings |
| 3 | Already enrolled with a different server (use `--force`) |
| 4 | The server rejected the token or deployment code |
| 5 | Not running elevated |
| 6 | Server unreachable. Settings were saved and the service keeps retrying |
| 7 | Another agent process could not be stopped |

## Example RMM script (deployment code)

```powershell
$ErrorActionPreference = 'Stop'
$server = 'https://patch.example.com'
$msi    = "$env:TEMP\EasyPatchAgent.msi"
Invoke-WebRequest "$server/api/v1/downloads/agent/EasyPatchAgent-1.0.0-x64.msi" -OutFile $msi -UseBasicParsing
$expected = '<sha256 from the console>'
if ((Get-FileHash $msi -Algorithm SHA256).Hash -ne $expected) { throw 'MSI hash mismatch' }
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart',
     "SERVERURL=$server", 'DEPLOYMENTCODE=EP-7Q4K-PZ2M', '/l*v', "`"$env:TEMP\EasyPatchAgent-install.log`"")
if ($p.ExitCode -notin 0, 3010) { throw "msiexec failed with $($p.ExitCode)" }
Start-Sleep -Seconds 20
& "$env:ProgramFiles\EasyPatch Agent\EasyPatch.Agent.exe" status
```

The console generates an equivalent script that also checks the Authenticode signature when
you require it.

## How the agent works

* **Check-in**: every 60 seconds (±10 % jitter; 15 seconds while a job is active). Picks up
  jobs, cancellations, configuration and credential rotation requests.
* **Scans**: every 6 hours (±10 %), shortly after the computer restarts, at service start when
  the last scan is older than that, after every install job, and on demand. Results and new
  Windows Update history entries are sent to the server. Background scans are skipped while
  another Windows Update operation of the agent is running.
* **Inventory**: daily and on demand. Includes OS edition, version and build, hardware model
  and serial number, disk space, domain membership, ESU licensing (volume licenses only), the
  Windows Update service state and the update source configuration.
* **Install jobs**: one at a time. Each job starts with a fresh scan, so a job that is repeated
  after a crash or restart never reinstalls anything. Servicing stack updates are installed
  first, feature updates last. If Windows needs a restart before it can install more, the job
  waits (state `WaitingForReboot`) and continues after somebody restarts the computer. If no
  restart happens within 30 days the job fails with `reboot_not_performed`.
* **Maintenance windows**: a scheduled job does not start new updates after the window closes.
  An update that is already installing is never interrupted.
* **Outages**: reports are written to disk before they are sent and are delivered in order when
  the server is reachable again. Retries use exponential backoff with full jitter (up to 15
  minutes) and honor `Retry-After`.
* **Clock**: requests are signed with a timestamp. The agent measures the server's clock and
  corrects its own offset automatically, so a wrong local clock does not break it.

## Logs and events

`C:\ProgramData\EasyPatch\logs\agent.log` rotates at 10 MB and keeps 10 files. Tokens, keys and
signatures are redacted before anything is written. To collect more detail, set the string
value `HKLM\SOFTWARE\EasyPatch\Agent\LogLevel` to `Debug` and restart the service; delete the
value to return to `Information`.

The Application event log (source *EasyPatch Agent*) receives warnings and errors and the
agent's informational events, including:

| Event ID | Meaning |
|---|---|
| 1000 | The service could not start (data directory or registry ACL, elevation, second instance) |
| 1001 | Enrollment request waiting for approval (includes the key fingerprint) |
| 1002 | Enrollment complete |
| 1003 | Enrollment refused or rejected |
| 1004 | The server revoked the agent's credential. Re-enroll to resume |
| 1005 | Credential rotated |

## Upgrading

Install the newer MSI over the existing one with the same command (no properties needed). The
server URL and proxy are kept, the identity in `C:\ProgramData\EasyPatch` is preserved and an
active job resumes after the service restarts. Downgrades are blocked.

An agent that never finished enrolling loses its token or deployment code on upgrade; pass it
again or run `enroll`.

## Uninstalling

```powershell
msiexec /x EasyPatchAgent-1.0.0-x64.msi /qn
```

or use *Apps & features*. The uninstaller stops the service, runs `unenroll` (the server marks
the endpoint as decommissioned when it can be reached) and removes the program. Logs in
`C:\ProgramData\EasyPatch\logs` are kept for troubleshooting; delete the folder if you do not
need them.

## Troubleshooting

| Symptom | Check |
|---|---|
| `status` shows `NotEnrolled` and the log says no token or deployment code | Install was done without `ENROLLMENTTOKEN`/`DEPLOYMENTCODE`, or the token was already consumed. Run `enroll` |
| Enrollment rejected (exit code 4, event 1003) | Token expired, revoked or used up, deployment code revoked, or the request was rejected by an administrator. Create a new token or code |
| Stays at `PendingApproval` | Approve the request in the console (*Pending enrollments*). Requests expire after 7 days; the agent then submits a new one |
| `Revoked` (event 1004) | The endpoint was removed or its credential revoked in the console. Re-enroll with `enroll --force` |
| "Could not reach the EasyPatch server" | DNS, firewall, proxy (`PROXYURL`), or TLS: the server certificate must chain to a root trusted by the computer |
| Scans fail with `0x8024402C`, `0x80244022` or similar | The Windows Update source (Microsoft or WSUS) is unreachable. The error code and its description are shown in the console |
| Updates not offered that you expected | WSUS approvals, Windows Update for Business deferrals or a target release version apply. The console shows the detected update source |

## Building from source

* Linux/macOS (build, test, publish, no MSI): `scripts/build-agent.sh --version 1.2.0 --rid all`
* Windows (also builds the MSI): `pwsh scripts/build-agent.ps1 -Version 1.2.0 -Architecture x64,arm64`

Signing is optional and is configured through environment variables, never through files in
the repository. See `agent/Installer/build-installer.ps1` for `EASYPATCH_SIGN_CERT_THUMBPRINT`,
`EASYPATCH_SIGN_PFX_PATH`/`EASYPATCH_SIGN_PFX_PASSWORD` and `EASYPATCH_SIGN_TIMESTAMP_URL`. Each
MSI is written to `artifacts/installer` together with a `.sha256` file. Put both in
`data/agent-packages` on the server to offer them for download.
