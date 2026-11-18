# NodeBB SSH Manager

A Windows desktop application for managing multiple NodeBB servers over SSH, with a Hebrew-first RTL interface.

## Implemented foundation

- .NET 11 / WPF application and Hebrew RTL dashboard.
- Multiple saved server profiles and persistent SSH sessions.
- SSH password and private-key authentication; optional credential remembering uses Windows DPAPI per-user encryption.
- SSH host-key SHA-256 pinning with explicit first-use approval and rejection of changed fingerprints.
- Interactive SSH terminal and SFTP browser with navigation, upload, download and confirmation before deletion.
- NodeBB discovery and lifecycle control through detected systemd, OpenRC, SysV, PM2, Docker or NodeBB CLI.
- File backups (full installation or configuration + uploads), with authenticated AES-256 encryption.
- MongoDB database backup scoped to the NodeBB database.
- Redis RDB backup with an explicit full-server scope warning; it includes all logical DBs on the Redis instance.
- PostgreSQL custom-format database backup using a protected temporary pgpass file; credentials are not passed as command-line arguments.
- MongoDB restore with archive validation, dry-run, pre-restore dump, database scoping and rollback attempt.
- Read-only database configuration inspector with masked credentials.
- NodeBB log viewer, read-only security audit, plugin/theme manager and server monitoring.
- System service inventory and controlled start/stop/restart with confirmation and privilege checks.
- Structured local diagnostic logging and ZIP report export.
- GitHub Actions build and self-contained Windows x64 publish artifact.

## NodeBB upgrades

The upgrade workflow supports Git-based installations with a clean working tree and an origin remote. It fetches tags, checks out an explicitly entered version tag, and runs ./nodebb upgrade while NodeBB is stopped. Before starting, the operator currently confirms that database and files/config/uploads backups exist. If the upgrade command fails, the application deliberately leaves NodeBB stopped because database schema migrations may already have run. The next development step is to make the upgrade window create and verify these backups itself, rather than relying on checkboxes.

## Database backup scope and limitations

- MongoDB: dump is scoped to the database configured by NodeBB.
- Redis: redis-cli --rdb snapshots the entire Redis server, including all logical databases and any other applications sharing that instance. The application warns about this; Redis restore is not offered.
- PostgreSQL: custom-format pg_dump of the database configured by NodeBB. Credentials are read from config.json remotely into a mode-600 pgpass file and are not placed in process arguments.
- PostgreSQL restore and automated cross-database restore workflows are not implemented yet.
- Backup files can contain sensitive configuration and must be encrypted before being shared or stored in an untrusted location.

## Security principles

- Never silently trust an unknown or changed SSH host key.
- Never store credentials in plaintext profile files or diagnostic logs.
- Encrypt exported backups by default.
- Use least privilege; do not assume root or sudo.
- Detect the remote operating system and service manager instead of assuming a particular Linux distribution.
- Quote and validate remote paths; never interpolate untrusted input into shell commands.
- Require explicit confirmation for destructive or service-impacting operations.
- Do not automatically bypass network filtering, server permissions or security controls.

## Technology

- C# / .NET 11, WPF, SSH.NET, local JSON profiles and GitHub Actions.

## Build locally

Requirements: Windows and the .NET 11 SDK.

dotnet restore .\NodeBB.SSHManager.sln

dotnet build .\NodeBB.SSHManager.sln -c Release

dotnet publish .\src\NodeBB.SSHManager.App\NodeBB.SSHManager.App.csproj -c Release -r win-x64 --self-contained true

## Current status

Development preview. Remote operations have not yet completed comprehensive live testing against different SSH servers, Linux distributions, service managers and NodeBB deployment methods. Do not use this preview for production administration or unverified destructive operations.

The roadmap includes automated pre-upgrade backup orchestration, scheduled backups, backup retention and validation, restore workflows for additional database engines, bilingual language switching and broader live compatibility testing.
