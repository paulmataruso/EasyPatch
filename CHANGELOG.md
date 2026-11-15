# Changelog

All notable changes to EasyPatch are listed here. Versions follow `MAJOR.MINOR.BUILD`; the server,
the web console and both agents share one version number.

## [1.5.1] - 2026-11-15

### Added

- **Skip certificate check** for the Windows install script (off by default), for servers with
  a private CA such as the lab setup. The one-liner and the script download without checking the
  server certificate; the script then passes the CA that the lab proxy publishes at
  `/server-ca.crt` to the MSI. The agent trusts that CA for its connection to EasyPatch only and
  keeps checking certificates (name, validity, chain). Use it only on a network you trust.
- MSI property `SERVERCACERT`: a certificate authority the agent trusts for its connection to the
  server only. It is remembered on upgrade.

### Changed

- The tray's status window, menus and restart prompts follow the Windows app mode (dark or light).
- Console: *Pending Approvals* is its own item in the sidebar (with the count of waiting
  computers), and *Agent Management* is renamed *Deploy* (enrollment tokens, deployment codes and
  agent packages). Old `/agents` links still work.

## [1.5.0] - 2026-11-15

Update the server before the Windows agents: a 1.5.0 agent reports update checks started from
the status window with the scan trigger `User`, which older servers reject.

### Added

- **Status window in the Windows tray.** Clicking the EasyPatch icon (or *Open EasyPatch
  status* in its menu) opens a window with a colored summary (up to date, updates available,
  restart required, busy, can't reach the server, waiting for approval, not enrolled), the
  details behind it (server connection and last check-in, the server's host name, last update
  check and the number of applicable updates, last installation, restart needed, current
  activity, computer name, agent version) and the newest 200 agent log entries (first lines
  only, redacted, with *Copy* for support tickets).
- **Check in now** and **Check for updates** in the status window. An update check is a scan
  only; it never installs anything and is reported with the scan trigger `User`. Both are
  limited for the whole computer: a check-in every 30 seconds, an update check every 5 minutes
  and not while Windows Update is busy.
- The icon's tooltip shows the agent version and the current status, and its menu shows the
  agent version.

### Changed

- The tray pipe carries larger frames from the service to the tray (up to 256 KB, for the
  log); frames from a tray are still limited to 16 KB.
- The `.deb` package's maintainer and the `.rpm` package's packager are now
  DHI Technical Services LLC (paulmataruso@dhitechnical.com).

## [1.4.0] - 2026-11-14

First public release. It contains everything below plus these additions.

### Added

- **Endpoint tags and rollout rings.** MSP-wide tags (for example *Pilot* or *Servers*) with their
  own policy layer and priority, and a **rollout delay** in policy with separate values for
  security and other updates, so a pilot ring gets updates first.
- **Alerts and notifications.** Eight built-in rules (endpoint offline, install failed, security
  update missing, known-exploited vulnerability unpatched, scan stale, restart pending, enrollment
  waiting for approval, unsupported operating system). Alerts resolve by themselves when the
  condition clears. Notifications go to email (SMTP), Slack, Microsoft Teams or a signed JSON
  webhook. Webhooks are limited to public HTTPS addresses unless private addresses are allowed.
- **CVE and CISA KEV prioritization.** Updates show the CVEs they fix: Windows from Microsoft's
  security update data, Debian from the Debian Security Tracker, and RHEL-family and Fedora from
  the dnf advisories. Updates fixing a vulnerability in the CISA Known Exploited Vulnerabilities
  catalog are flagged, listed first and, by default, skip the rollout delay.
- **Monthly PDF compliance reports** per organization, generated on demand or emailed on a chosen
  day over STARTTLS or TLS, with an optional *Prepared by* brand name.
- **Third-party application updates on Windows (opt-in)** with winget: upgrades of already
  installed applications on an approved list of package IDs, from the winget community repository
  only, counted as installed only when winget lists the new version.
- **SMTP: skip certificate validation** (off by default, audited), for internal mail servers with
  self-signed certificates.

### Fixed

- Windows feature updates are no longer shown as fixing the CVEs of the cumulative update they
  share a KB number with.

## [1.3.2] - 2026-11-14

### Added

- Linux agent: dnf5 support (Fedora 41 and later), tolerance of `systemd` re-executing during
  upgrades, and newer lifecycle dates for Ubuntu 26.04, Debian 13, EL10 and Fedora.
- The Linux install script can reinstall an agent that is not enrolled.

## [1.3.0] - 2026-11-14

### Added

- **Linux agent** for Debian and Ubuntu (apt) and Red Hat Enterprise Linux, Rocky Linux,
  AlmaLinux, Oracle Linux, CentOS Stream and Fedora (dnf), as signed `.deb` and `.rpm` packages
  for x64 and ARM64, with a one-line install script for deployment codes. It installs upgrades of
  installed packages at exact versions and uses the same policies, jobs, restarts and compliance
  reporting as Windows.

## [1.2.0] - 2026-11-14

### Added

- **Agent self-update** from packages signed with your own release key (ECDSA P-256). The public
  key is built into the agent, which verifies every package itself and never downgrades. An
  *Agent updates* policy (Automatic or Manual) at global, organization, site and endpoint level,
  plus *Update agent* for one or many endpoints.

## [1.1.1] - 2026-11-13

### Fixed

- The tray icon did not connect for users without administrator rights.

## [1.1.0] - 2026-11-13

### Added

- Tray icon for every signed-in user, install notifications and preset messages to users (no
  free text).
- Restarts of one or many endpoints, now, at a chosen time or on a recurring schedule, with a
  countdown or a postpone deadline for signed-in users. A restart job completes only when the boot
  time shows that the computer restarted.
- Windows Update policy overrides per organization, site or endpoint (Automatic Updates mode,
  feature and quality update deferral, pausing, restart behaviour and active hours). Each group is
  *Not configured* by default, conflicts with Group Policy, WSUS or Intune are reported, and the
  original values are restored when an override is removed.
- Policy can be set per endpoint.

## [1.0.0] - 2026-11-13

### Added

- Server (ASP.NET Core, PostgreSQL), web console (React) and Windows agent (Windows Update Agent
  API) with an MSI installer.
- Organizations, sites and inherited policies; enrollment tokens and tokenless deployment codes
  with administrator approval; per-device ECDSA agent credentials with signed requests.
- Scans, inventory, install jobs with a durable queue, verification of installed updates by scan,
  maintenance windows, compliance status, OS support status and CSV reports.
- Argon2id passwords, mandatory TOTP MFA, roles and an audit log.

[1.4.0]: https://github.com/paulmataruso/EasyPatch/releases/tag/v1.4.0
