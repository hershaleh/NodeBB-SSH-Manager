# NodeBB SSH Manager

A Windows desktop application for managing multiple NodeBB servers over SSH, with a Hebrew-first RTL interface.

## Implemented foundation

- .NET 11 / WPF application and Hebrew RTL dashboard.
- Multiple saved server profiles.
- SSH password and private-key authentication.
- Passwords and key passphrases are requested at connection time and are not stored in the profile JSON.
- SSH host-key SHA-256 pinning with explicit first-use approval and rejection of changed fingerprints.
- Persistent SSH sessions for multiple servers.
- Interactive SSH terminal.
- SFTP browser with navigation, upload, download and confirmation before deletion.
- Initial NodeBB discovery and CLI lifecycle actions (start, stop, restart, build).
- Structured local diagnostic logging and ZIP report export.\n- Read-only server monitoring for uptime, load, memory, disk usage and top processes.\n- Read-only remote capability inventory: OS, kernel, architecture, init/service manager, package manager, user privileges, Node.js/npm/Git, MongoDB/Redis, Docker/Podman and PM2.
- GitHub Actions build and self-contained Windows x64 publish artifact.

## Diagnostic and error-reporting system

Application logs are stored locally under:

`%LocalAppData%\NodeBB SSH Manager\Logs`

The log records UTC timestamps, severity, component, event, server label (when relevant), error details and exception stack traces. Log files are rotated and retained in a bounded number.

Use **דוח אבחון** in the main window to export a ZIP containing recent logs and a small runtime manifest. The export is local and manual; nothing is uploaded automatically. Server profiles, credentials, private keys, environment variables and remote file contents are not intentionally included. Known credential patterns are redacted before logging and again before export.

Do not share diagnostic reports publicly without reviewing them first. A report can still contain server names, hostnames, paths or other operational metadata.

## Planned full product scope

The application is being developed toward a comprehensive multi-server management suite:

- Server inventory, capability detection and connection health.
- NodeBB installation discovery, configuration inspection, lifecycle and upgrades.
- Plugin and theme installation, update, removal and compatibility checks.
- Remote file management, safe editing, permissions and transfers.
- MongoDB / Redis discovery and database maintenance workflows.
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

The next development milestones are broader server capability detection, safer NodeBB lifecycle handling, backup/database/plugin modules, and automated tests for the service layer.
