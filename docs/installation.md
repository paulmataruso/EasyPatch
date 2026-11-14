# Installing the EasyPatch server

The EasyPatch server is a Docker Compose stack with four long-running containers (`caddy`,
`api`, `worker`, `postgres`) and one one-shot container (`migrate`). See
[architecture.md](architecture.md#6-docker-deployment-design) for the design. This guide covers
installation, first sign-in, agent packages, upgrades, backup and restore.

## Requirements

| | |
|---|---|
| Host | 64-bit Linux with Docker Engine and the Docker Compose plugin. The development override (`docker-compose.dev.yml`) uses the `!override` tag, which needs Compose 2.24.4 or later |
| DNS | A public DNS name (A or AAAA record) pointing at the host, for example `patch.example.com` |
| Inbound | TCP 443 from administrators and from every managed endpoint. TCP 80 from the internet, for ACME certificate validation and the HTTP to HTTPS redirect |
| Outbound | The ACME certificate authority (Caddy's defaults). If you use alert notifications: your SMTP server (usually TCP 587 or 465) and the webhook hosts (TCP 443, for example `hooks.slack.com` or your Teams workflow host), from the `worker` container only. Building the images also needs access to `mcr.microsoft.com`, Docker Hub, `api.nuget.org` and `registry.npmjs.org` |

Nothing else is published. PostgreSQL is attached only to an internal Docker network and has no
host port.

## 1. Get the source

```bash
git clone <repository-url> easypatch
cd easypatch
```

## 2. Prepare secrets and configuration

```bash
./scripts/init-secrets.sh
```

The script is safe to re-run and never overwrites existing files. It creates:

* `secrets/db_password`: a random 64-character database password, used as a Compose secret.
  The `secrets/` directory is mode 700. The password never appears in `.env`, environment
  variables or image layers;
* `data/agent-packages/`: the directory from which agent MSIs are offered for download;
* `.env`, copied from `.env.example` (mode 600).

Edit `.env`:

| Variable | Required | Meaning |
|---|---|---|
| `EASYPATCH_DOMAIN` | yes | The public DNS name. Browsers and agents connect to `https://<EASYPATCH_DOMAIN>` |
| `ACME_EMAIL` | yes | Contact address given to the certificate authority |
| `EASYPATCH_VERSION` | no | Tag for the locally built images (default `1.0.0`) |
| `EASYPATCH_PUBLIC_URL` | no | Overrides the URL used in install commands, reset links and scripts. Defaults to `https://<EASYPATCH_DOMAIN>` |
| `EASYPATCH_EDGE_SUBNET` | no | Subnet of the network between Caddy and the API (default `172.30.0.0/24`). Change it only if it collides with an existing network. The API trusts `X-Forwarded-For` only from this subnet |

`.env` holds no passwords. `secrets/`, `data/`, `backups/` and `.env` are ignored by git.

## 3. Build and start

```bash
docker compose build
docker compose up -d
docker compose ps
```

Start-up order is enforced by health checks: `postgres` becomes healthy, `migrate` applies the
database migrations and exits, then `api` and `worker` start, and `caddy` starts once the API is
ready. Caddy requests the certificate on first start; follow it with
`docker compose logs -f caddy`.

## 4. Create the first administrator

```bash
docker compose run --rm api create-admin --username admin --email admin@example.com
```

This creates a SuperAdmin and prints a temporary password once. The password is not logged.
To set the password yourself instead, pass `--password-stdin` and supply it on standard input
(use `-T` so Compose passes stdin through):

```bash
docker compose run --rm -T api create-admin --username admin --email admin@example.com --password-stdin
```

## 5. First sign-in

Open `https://<EASYPATCH_DOMAIN>` and sign in with the temporary password. Before anything else
the console requires you to:

1. **Set up multi-factor authentication**: scan the QR code with an authenticator app, enter a
   code, then store the recovery codes it shows once.
2. **Choose a new password**: 12 to 256 characters with at least 4 distinct characters. It may
   not contain the username.

MFA is required for every user while the server setting `RequireMfa` is on (the default).

Then:

* create other users under **Settings → Users** (roles: SuperAdmin, Admin, ReadOnly, optionally
  restricted to specific organizations);
* review **Settings → Global policy** and **Settings → Server settings**;
* to receive alerts by email or chat, set up the SMTP server and channels under
  **Settings → Notifications**, then select the channels in **Settings → Alert rules** (see
  [alerts.md](alerts.md)). Without channels, alerts are only shown on the **Alerts** page;
* create an organization and its sites under **Organizations**.

## 6. Offer agent packages

Build the agent MSI (see [agent-installation.md](agent-installation.md#building-from-source))
and the Linux `.deb` and `.rpm` packages (see [linux-agent.md](linux-agent.md#building-from-source)),
and copy them, each with its `.sig` release signature, to `data/agent-packages/`:

```bash
cp EasyPatchAgent-1.3.0-x64.msi EasyPatchAgent-1.3.0-x64.msi.sig data/agent-packages/
cp artifacts/linux/EasyPatchAgent-1.3.0-*.deb* artifacts/linux/EasyPatchAgent-1.3.0-*.rpm* data/agent-packages/
chmod 644 data/agent-packages/*
```

* File names must match `EasyPatchAgent-<version>-<x64|arm64>.<msi|deb|rpm>` (version
  `MAJOR.MINOR.BUILD`, optionally with a fourth part). Other files starting with `EasyPatchAgent-`
  are ignored and a warning is logged; `.sig` and `.sha256` files are not packages.
* The API runs as uid 1654 and mounts the directory read-only, so the files must be
  world-readable.
* The server computes each package's SHA-256 itself. The package list is refreshed at most once a
  minute.
* Packages appear under **Agent Management → Packages**. The console uses the newest MSI to
  build `msiexec` commands and the PowerShell install script for deployment codes. That script
  verifies the package's SHA-256, and optionally its Authenticode signature, before installing.
  The Linux install script offers the newest `.deb` and `.rpm` for each architecture and
  verifies the SHA-256 of the one it downloads.
* Agents (Windows 1.2.0 and later, all Linux agents) update themselves to the newest **signed**
  package in their own format and architecture (one with a valid `.sig` file): automatically when the *Agent updates* policy is Automatic (the default), or when
  an Operator chooses *Update agent…*. Copying a newer signed package here therefore starts a
  rollout. Set the policy to Manual first if you want to choose when. Agents check the release
  signature against the key built into them, so the server cannot make them install anything
  else. See [agent-installation.md](agent-installation.md#upgrading). Older agents only log that
  a newer version exists; install the MSI on them once.
* The release private key belongs on the machine that builds the agent. Never copy it to the
  server: the server needs only the packages and their `.sig` files.

Agent installation and enrollment are described in [agent-installation.md](agent-installation.md)
(Windows) and [linux-agent.md](linux-agent.md) (Linux).

## Upgrading

Back up first, then rebuild and restart:

```bash
./scripts/backup.sh
git pull
docker compose build
docker compose up -d
```

`migrate` runs again on `up -d` and applies any new migrations before `api` and `worker` start.
Agents keep working through the restart. Each agent stores its unsent reports in an outbox and
retries them, and jobs stay queued in the database.

Use the `docker-compose.yml` of the new version: it may add networks or volumes. For example,
the release with alerts gives the `worker` the `dpkeys` volume (read-only) and the outbound
`egress` network. The upgrade adds seven default alert rules without notification channels.

## Backup and restore

```bash
./scripts/backup.sh
```

This writes `backups/<UTC timestamp>/`, created with umask 077, containing:

| File | Contents |
|---|---|
| `easypatch.dump` | `pg_dump` custom-format database dump |
| `dpkeys.tar` | ASP.NET Data Protection keys. **Without these, MFA secrets and notification secrets (SMTP password, webhook URLs and signing keys) in the dump cannot be decrypted: every user must enroll MFA again and the secrets must be entered again** |
| `agent-packages.tar` | The agent MSIs |
| `SHA256SUMS` | Checksums of the three files |

The stack must be running (the script uses the `postgres` and `api` containers). Copy backups off
the host and protect them. The dump contains hashed credentials and agent public keys, and
`dpkeys.tar` decrypts MFA secrets and notification secrets. Keep them in separate locations if you can.

To restore:

```bash
./scripts/restore.sh backups/20261113T020000Z --yes
```

The script:

1. verifies `SHA256SUMS`;
2. stops `caddy`, `api` and `worker`;
3. restores the database with `pg_restore --clean --if-exists`, then replaces the key ring and the
   agent packages;
4. starts the stack again.

**The restore replaces the current database.** The stack must have been started at least once
so that the volumes exist. `secrets/db_password` is not part of the backup. To restore onto a new
host, first follow steps 1 to 3 above (which initialize a database with that host's own
password), then run `restore.sh`. You do not need to create an administrator: the restored
database contains the users.

## Operational commands

The API image includes administration commands. Run them with
`docker compose run --rm api <command>`:

| Command | Purpose |
|---|---|
| `migrate` | Apply database migrations (the `migrate` service runs this automatically) |
| `create-admin --username <name> --email <email> [--password-stdin]` | Create a SuperAdmin |
| `reset-password --username <name> [--password-stdin]` | Reset a password and revoke the user's sessions. A generated password must be changed at next sign-in |
| `reset-mfa --username <name>` | Remove a user's MFA and revoke their sessions. They enroll again at next sign-in |
| `unlock --username <name>` | Clear a login lockout |
| `help` | List the commands |

These are for recovery when no SuperAdmin can sign in. Day-to-day, a SuperAdmin uses
**Settings → Users**. `reset-password`, `unlock` and the console actions are recorded in the
audit log.

## Rate limits and other configuration

Per-client rate limits can be changed with environment variables on the `api` service, for
example in a `docker-compose.override.yml`, which Compose loads automatically:

```yaml
services:
  api:
    environment:
      RateLimits__LoginPerMinute: "20"     # per IP: login, MFA and password routes
      RateLimits__EnrollPerMinute: "30"    # per IP: agent enrollment
      RateLimits__AgentPerMinute: "120"    # per agent credential
      RateLimits__DownloadsPerMinute: "60" # per IP: package and install-script downloads
      RateLimits__AdminPerMinute: "600"    # per user (or IP when anonymous): admin API
```

The values shown are the defaults. Everything else, including job limits, offline threshold,
credential lifetime, retention and session timeouts, is under **Settings → Server settings**.

## Development and lab mode

`docker-compose.dev.yml` replaces ACME with Caddy's internal certificate authority
(`local_certs`), and publishes HTTPS only on `127.0.0.1:8443` (HTTP is not published).

```bash
# in .env: EASYPATCH_DOMAIN=localhost (ACME_EMAIL must be set but is not used)
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
docker compose -f docker-compose.yml -f docker-compose.dev.yml run --rm api create-admin --username admin --email admin@example.com
```

Open `https://localhost:8443` and accept the certificate warning, or trust Caddy's root
certificate. To use a different port, set `EASYPATCH_HTTPS_PORT`.

To reach a lab server from other machines on a private network, set both the address and the
name to the host's LAN IP address in `.env`, for example `EASYPATCH_BIND_ADDRESS=192.168.1.20`
and `EASYPATCH_DOMAIN=192.168.1.20`. Then open `https://192.168.1.20:8443`. Caddy's internal CA
issues a certificate for the IP address. Anyone who can reach that address can reach the sign-in
page, so do this only on a trusted network. Agents only connect to a server
whose certificate chains to a root trusted by Windows. A lab agent therefore needs Caddy's root
certificate in the computer's Trusted Root store. Do not use this mode in production.

## Building and testing from source

Building requires the .NET SDK version pinned in `global.json` and Node.js 24.

```bash
dotnet build EasyPatch.slnx -c Release
dotnet test backend/Tests/EasyPatch.Tests.csproj           # needs Docker (Testcontainers PostgreSQL)
dotnet test agent/EasyPatch.Agent.Tests/EasyPatch.Agent.Tests.csproj

cd frontend
npm ci
npm run typecheck && npm test && npm run build
```

`scripts/build-msi-linux.sh --arch all` builds the agent MSIs with Docker (see
[agent-installation.md](agent-installation.md#building-from-source)).
`scripts/audit-dependencies.sh` lists known-vulnerable NuGet packages, including transitive
ones, and runs `npm audit`.
