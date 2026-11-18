# NodeBB SSH Manager

A Windows desktop application for managing multiple NodeBB servers over SSH, with a Hebrew-first RTL interface.

## Implemented foundation

- .NET 11 / WPF application and Hebrew RTL dashboard.
- Multiple saved server profiles.
- SSH password and private-key authentication.
- Passwords and key passphrases are requested at connection time; optional remembering uses Windows DPAPI per-user encryption in a separate vault, never the profile JSON.
- SSH host-key SHA-256 pinning with explicit first-use approval and rejection of changed fingerprints.
- Persistent SSH sessions for multiple servers.
- Interactive SSH terminal.
- SFTP browser with navigation, upload, download and confirmation before deletion.
- NodeBB discovery and lifecycle control through detected systemd, OpenRC, SysV, PM2, Docker or the NodeBB CLI, with privilege checks and safe fallbacks.
- Downloadable file backups (full installation or configuration + uploads), with archive verification and authenticated AES-256 encryption enabled by default; database contents are explicitly excluded.
- Read-only NodeBB database configuration inspector that masks credentials and connection URIs.
- NodeBB log viewer with refresh, local text filtering and copy.
- Read-only remote security audit for SSH policy, account privileges, firewall status, listening ports and NodeBB file permissions.
- Plugin/theme package inventory and controlled install, update and removal; plugin activate/deactivate actions with package-name validation and confirmation.
- Structured local diagnostic logging and ZIP report export.
- Read-only server monitoring for uptime, load, memory, disk usage and top processes.
- System service inventory and controlled start/stop/restart for systemd, OpenRC and SysV, with explicit confirmation and non-interactive privilege checks.
- Read-only remote capability inventory: OS, kernel, architecture, init/service manager, package manager, user privileges, Node.js/npm/Git, MongoDB/Redis, Docker/Podman and PM2.
- GitHub Actions build and self-contained Windows x64 publish artifact.

## NodeBB upgrades\n\nThe upgrade workflow supports Git-based installations with a clean working tree and an `origin` remote. It fetches tags, checks out an explicitly entered version tag, and runs `./nodebb upgrade` while NodeBB is stopped. Before starting, the operator must confirm that database and files/config/uploads backups exist. If the upgrade command fails, the application deliberately leaves NodeBB stopped; it does not claim automatic database rollback because schema migrations may already have run. See the [official NodeBB upgrade guide](https://docs.nodebb.org/configuring/upgrade/).\n\n## Database backup scope\n\nMongoDB backup is scoped to the NodeBB database. Redis `redis-cli --rdb` produces a snapshot of the entire Redis server, including every logical database and any other applications sharing that Redis instance. The UI requires an explicit warning confirmation before creating this full-server snapshot. Redis backup files are encrypted locally using the same authenticated backup format. Redis restore is intentionally not offered yet because safely replacing a shared Redis instance requires an isolated maintenance plan and explicit impact analysis.\n\n## Diagnostic and error-reporting system

Application logs are stored locally under:

`%LocalAppData%\NodeBB SSH Manager\Logs`

The log records UTC timestamps, severity, component, event, server label (when relevant), error details and exception stack traces. Log files are rotated and retained in a bounded number.

Use **דוח אבחון** in the main window to export a ZIP containing recent logs and a small runtime manifest. The export is local and manual; nothing is uploaded automatically. Server profiles, encrypted credential vault, credentials, private keys, environment variables and remote file contents are not included. Known credential patterns are redacted before logging and again before export.

Do not share diagnostic reports publicly without reviewing them first. A report can still contain server names, hostnames, paths or other operational metadata.

## Planned full product scope

The application is being developed toward a comprehensive multi-server management suite:

- Server inventory, capability detection and connection health.
- NodeBB installation discovery, configuration inspection, lifecycle and guarded Git-tag upgrades.
- Plugin and theme installation, update, removal and compatibility checks.
- Remote file management, safe editing, permissions and transfers.
- MongoDB encrypted backup and guarded restore with preflight, pre-restore dump and rollback.\n- Redis encrypted full-server RDB backup (all logical databases on the configured Redis server; restore is not yet implemented).
- Full and component backups, restore validation, retention and scheduling.
- Server monitoring, process inspection, resource usage and diagnostics.
- Service manager detection (systemd, OpenRC, SysV, containers, PM2 and manual processes) with safe fallbacks.
- Security checks, SSH configuration review and permission audits.
- Centralized logs and exportable diagnostic bundles.
- Interactive SSH terminal and scheduled automation.
- Hebrew / English language switching and RTL / LTR layouts.
- Optional, separately secured and versioned server-side helper for operations that cannot be performed safely or efficiently over standard SSH.

The planned list is a product roadmap, not a claim that every module is already implemented. Each operation will report unsupported or unavailable capabilities rather than pretending to succeed.

## Security principles

- Never silently trust an unknown or changed SSH host key.
- Never store credentials in plaintext profile files or diagnostic logs.
- Encrypt exported backups by default because NodeBB configuration files can contain database credentials.
- Use least privilege; do not assume root or sudo.
- Detect the remote operating system and service manager instead of assuming a particular Linux distribution.
- Quote and validate remote paths; never interpolate untrusted input into shell commands.
- Require explicit confirmation for destructive or service-impacting operations.
- Keep diagnostic export local and user-initiated.
- Do not automatically bypass network filtering, server permissions or security controls.

## Technology

- C# / .NET 11
- WPF
- SSH.NET for SSH, shell streams and SFTP
- Local JSON server-profile storage
- GitHub Actions for Windows x64 build and self-contained publish

## Build locally

Requirements: Windows and the .NET 11 SDK.

```powershell
dotnet restore .\NodeBB.SSHManager.sln
dotnet build .\NodeBB.SSHManager.sln -c Release
dotnet publish .\src\NodeBB.SSHManager.App\NodeBB.SSHManager.App.csproj -c Release -r win-x64 --self-contained true
```

## Current status

Development preview. The current remote-management functions have not yet completed comprehensive live testing against different SSH servers, Linux distributions, service managers and NodeBB deployment methods. Do not use this preview for production administration or unverified destructive operations.

The remaining roadmap includes Redis/PostgreSQL database support, NodeBB upgrade workflows, scheduled operations, bilingual language switching, and broader live compatibility testing.


### Database backups
- MongoDB: encrypted remote database backup using MongoDB Database Tools and NodeBB's configured connection. Secrets are written to a protected temporary config file on the server, never command-line arguments. Redis/PostgreSQL backup and restore are not implemented yet. MongoDB restore validates the encrypted archive locally, runs a remote dry-run, creates a pre-restore dump, scopes the restore to the configured NodeBB database, and attempts automatic rollback if import fails. If rollback itself fails, the remote rollback directory is deliberately preserved for manual recovery.
