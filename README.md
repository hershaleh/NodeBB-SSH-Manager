# NodeBB SSH Manager

A modern Windows desktop application for securely managing multiple NodeBB servers over SSH.

## Current implementation

The first development milestone is in place:

- .NET 11 WPF desktop application shell with a Hebrew RTL dashboard.
- Multiple server profiles stored locally under the current Windows user's Local Application Data folder.
- Add and remove server profiles.
- SSH password authentication and private-key authentication.
- Passwords and key passphrases are requested when connecting and are not written to the server profile file.
- SSH host-key SHA-256 fingerprint verification, including explicit first-use approval and a warning when a pinned fingerprint changes.
- SSH connection test that reports the remote username and operating-system family.
- GitHub Actions build and self-contained Windows x64 publish artifact.

The connection test currently opens a temporary SSH session and closes it after verification. Persistent sessions, SFTP browsing, the full management modules, secure credential vault integration, and the optional Server Helper are subsequent milestones.

## Goals

- Manage multiple independent NodeBB server profiles.
- Discover server capabilities instead of assuming a specific Linux distribution or service manager.
- Provide NodeBB lifecycle, plugin/theme, file, database, backup, monitoring, diagnostics, and SSH terminal workflows.
- Support Hebrew (RTL) and English (LTR).
- Keep privileged server operations explicit, auditable, and least-privilege.
- Treat the optional Server Helper as a separately versioned, secured component.

## Technology

- C# / .NET 11
- WPF desktop UI
- SSH and SFTP transport via SSH.NET
- Windows Credential Manager / DPAPI for future persistent secret storage
- SQLite for future local application data

## Repository layout

- `src/NodeBB.SSHManager.App` — Windows desktop application
- `tests/` — automated tests (to be added with the service layer)
- `.github/workflows/` — CI build

## Build

Requirements: Windows, .NET 11 SDK.

```powershell
dotnet restore .\NodeBB.SSHManager.sln
dotnet build .\NodeBB.SSHManager.sln -c Release
```

## Security principles

- Verify and persist SSH host fingerprints; never silently trust changed host keys.
- Do not store passwords or key passphrases in plaintext configuration files.
- Do not expose secrets in logs, diagnostics, or exception messages.
- Use least privilege. Never assume root access.
- Validate inputs and avoid constructing shell commands from untrusted strings.
- Require explicit confirmation for destructive or service-impacting actions.
- Report unavailable capabilities honestly; never simulate a successful operation.

## CI artifact

The Windows Build workflow restores and builds the solution, then publishes a self-contained Windows x64 artifact. Open the repository's **Actions** tab and download the artifact from a successful run.

## Project status

Early development preview. Do not use it yet for production server administration.


## Remote management milestone

The current implementation now includes the first operational remote-management layer:

- Persistent SSH sessions for multiple saved server profiles (session secrets remain in process memory and are not written to the profile file).
- Interactive SSH terminal window using an SSH shell stream.
- SFTP file browser with directory navigation, upload, download, and explicit-confirmation deletion.
- NodeBB discovery in common Linux locations (`/var/www`, `/opt`, `/srv`, and `/home`, up to five directory levels).
- NodeBB CLI actions: start, stop, restart, and build, each with an explicit confirmation.
- SSH host-key pinning is applied to persistent SSH and SFTP connections.

Discovery is intentionally bounded to common paths to avoid an unbounded full-filesystem scan. It does not assume systemd or root access; NodeBB lifecycle actions use the installation's `./nodebb` CLI and therefore require the SSH account to have the necessary permissions.

The SFTP and terminal windows require an active SSH session. Passwords and key passphrases are held only in memory for the active session and are not persisted to disk. This milestone still needs a successful CI build and live testing against representative Linux/NodeBB servers before being considered production-ready.
