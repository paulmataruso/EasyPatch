<p align="center">
  <img src="frontend/public/logo.svg" width="112" alt="">
</p>
<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="frontend/public/wordmark.svg">
    <img src="frontend/public/wordmark-light.svg" height="72" alt="EasyPatch">
  </picture>
</h1>

EasyPatch is a self-hosted Windows patch management platform for Managed Service Providers.
A small Windows agent reports Windows Update status to a central web console. From there an
administrator installs the missing updates on one computer, a site, or a whole customer, and
tracks compliance across every customer.

* **Outbound-only agents.** Endpoints connect to the server over HTTPS (TCP 443). No VPN,
  inbound firewall rule or port forwarding is needed, so agents work behind NAT.
* **Patching, and nothing else.** The agent has no remote shell and no command execution. It
  runs a fixed set of job types: update scan, inventory refresh, install updates, restart, and
  preset messages to signed-in users.
* **No surprise restarts.** EasyPatch reports when a restart is pending and why. It restarts a
  computer only when an administrator asks for it, either once or through a restart schedule
  the administrator configured, and it warns signed-in users first. Installing updates never
  restarts a computer.
* **Respects existing configuration.** WSUS, Group Policy and Windows Update for Business
  settings are detected and reported. EasyPatch changes Windows Update policy settings only
  when an administrator configures an override. It never touches the WSUS server settings, and
  it puts the original values back when the override is removed.

## Features

* **Multi-tenant hierarchy**: Organization → Site → Endpoint. Policies are inherited, with
  overrides at organization, site and endpoint level. Administrators can be restricted to
  specific organizations.
* **Install all missing updates** on one endpoint or on many selected endpoints at once, or
  install chosen updates. A single update can also be rolled out across an organization or a
  site. Installing on a single endpoint first shows exactly which updates will be installed.
  Policy decides which categories are included: by default, everything except drivers and
  feature updates.
* **Optional scheduled installs** inside a per-policy maintenance window (off by default),
  limited per site and globally.
* **Restarts** of one endpoint or many selected endpoints, now or at a chosen time, or on a
  recurring restart schedule set in policy (optionally only when Windows needs a restart).
  Signed-in users see a countdown, or can postpone up to a deadline. A restart job completes
  only when the computer's boot time shows that it actually restarted.
* **Tray icon and user messages.** The agent shows an EasyPatch icon in the notification area
  of every signed-in user, with its status and the time of its last contact with the server.
  Users are told when updates are being installed (this can be turned off in policy), and
  administrators can send preset messages. There is no free-text messaging.
* **Windows Update policy overrides** per organization, site or endpoint: Automatic Updates
  mode, feature update deferral and target version, quality update deferral and pausing, and
  restart behaviour with active hours. Each group is *Not configured* by default. The console
  shows whether each endpoint applied the settings, or whether another policy source such as
  domain Group Policy overrides them.
* **Durable job queue** in PostgreSQL. Jobs survive endpoint disconnects and server restarts.
  The agent acknowledges each delivery, and unacknowledged jobs are delivered again. Each job
  records who requested it, its delivery, start and completion times, and per-update results.
* **Verified results.** An update counts as installed only after a Windows Update scan confirms
  it. Updates waiting for a restart are tracked as such.
* **Compliance reporting**: per-endpoint status, organization trends from daily snapshots, OS
  support status (including Windows 10 ESU), and CSV exports. See
  [docs/compliance.md](docs/compliance.md).
* **Two enrollment methods**:
  * limited-use, expiring enrollment tokens;
  * tokenless deployment codes for RMM scripts. Each computer that uses one waits for an
    administrator to approve it, after the administrator compares key fingerprints.
* **Strong admin security**:
  * Argon2id password hashing and mandatory TOTP MFA with recovery codes;
  * server-side sessions, CSRF protection, lockout and rate limits;
  * three roles: SuperAdmin, Admin, ReadOnly;
  * an audit log of administrative actions.
* **Per-device agent credentials.** Each agent generates its own ECDSA P-256 key pair; only the
  public key goes to the server. Every request is signed with a timestamp and nonce for replay
  protection, and keys rotate automatically. The agent contains no shared secret.

## Architecture

```
Windows agents ──HTTPS──┐
                        ├──► caddy (TLS, web console) ──► api ──► postgres
Admin browsers ──HTTPS──┘                                  worker ──┘
```

| Component | Technology |
|---|---|
| Server API and worker | ASP.NET Core 10, EF Core 10, PostgreSQL 18 |
| Web console | React 19, TypeScript, Vite, Tailwind CSS (served by Caddy) |
| Reverse proxy | Caddy 2 with automatic certificates |
| Agent | .NET 10 Windows service using the Windows Update Agent COM API, plus a tray icon (Windows Forms) in each user session |
| Installer | WiX Toolset v5 MSI (x64 and ARM64) |
| Deployment | Docker Compose: `caddy`, `api`, `worker`, `postgres`, plus a one-shot `migrate` |

See [docs/architecture.md](docs/architecture.md) for the full design.

## Quick start

On a Linux host with Docker, a DNS name pointing at it, and ports 80 and 443 open:

```bash
git clone <repository-url> easypatch && cd easypatch
./scripts/init-secrets.sh                 # database password secret, data directories, .env
$EDITOR .env                              # set EASYPATCH_DOMAIN and ACME_EMAIL
docker compose build
docker compose up -d
docker compose run --rm api create-admin --username admin --email admin@example.com
```

Then:

1. Open `https://<EASYPATCH_DOMAIN>`, sign in with the printed temporary password, set up MFA
   and choose a new password.
2. Copy an agent MSI into `data/agent-packages/`.
3. Create an organization, then deploy agents with an enrollment token or a deployment code.

The full guides are [docs/installation.md](docs/installation.md) and
[docs/agent-installation.md](docs/agent-installation.md).

## Documentation

| Document | Contents |
|---|---|
| [installation.md](docs/installation.md) | Server installation, first sign-in, agent packages, upgrades, backup and restore, CLI commands |
| [agent-installation.md](docs/agent-installation.md) | Agent requirements, MSI properties, RMM deployment, command line, logs, building the MSI |
| [architecture.md](docs/architecture.md) | Components, data model, update state model, policy, job system, agent lifecycle, deployment design |
| [compliance.md](docs/compliance.md) | Compliance statuses and rules, required updates, OS support status, reports |
| [security.md](docs/security.md) | Security model, threat model, hardening checklist, known limitations |
| [api.md](docs/api.md) | REST API (admin and agent protocol). The live OpenAPI document is served at `/api/docs` to signed-in administrators |
| [troubleshooting.md](docs/troubleshooting.md) | Diagnosing server, sign-in, agent and job problems |

## Development

Requirements: the .NET SDK pinned in `global.json` (10.0.404 or a later feature band), Node.js
24, and Docker (the integration tests start PostgreSQL with Testcontainers).

```bash
dotnet build EasyPatch.slnx -c Release
dotnet test backend/Tests/EasyPatch.Tests.csproj                 # unit + integration tests
dotnet test agent/EasyPatch.Agent.Tests/EasyPatch.Agent.Tests.csproj

cd frontend && npm ci && npm run typecheck && npm test && npm run build
```

The backend integration tests run against a real PostgreSQL database. They include an
end-to-end lifecycle test, which drives the real agent runtime (with the Windows-specific
services replaced by test doubles) against the real API: enrollment, scan, install, restart,
verification and compliance.

The agent builds and tests on Linux with `scripts/build-agent.sh`. The MSIs can be built on
Linux with Docker (`scripts/build-msi-linux.sh`, which runs WiX under Wine) or on Windows with
`scripts/build-agent.ps1`, which can also sign them. The `agent` GitHub Actions workflow builds
them on Windows.

```
shared/      Contracts shared by agent and server (DTOs, request signing, update classification)
backend/     Core (domain), Infrastructure (EF Core, services), API, Worker, Tests
agent/       Agent core, communication, Windows Update, inventory, service host, tests, WiX installer
frontend/    Web console
deployment/  Dockerfiles and Caddy configuration
scripts/     init-secrets, backup, restore, agent and MSI builds, agent icon, dependency audit
docs/        Documentation
```

## Project status

Version 1.0.0, pre-release. Server, console and agent logic are covered by automated tests on
Linux. The following have not yet been validated by this repository's automated tests:

* the Windows-specific agent components on real Windows: Windows Update Agent COM calls, WMI
  inventory, DPAPI key storage and the Windows service host;
* the MSI on real Windows. It builds on Linux, and installs, upgrades and uninstalls under
  Wine, but Windows Installer ICE validation has not been run;
* the PowerShell install script on Windows.

Validate them on test endpoints before rolling out to production.

## License

[Apache License 2.0](LICENSE)
