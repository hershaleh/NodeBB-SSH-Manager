# NodeBB SSH Manager

A modern Windows desktop application for securely managing multiple NodeBB servers over SSH.

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
- SSH and SFTP transport (implementation milestone in progress)
- Windows Credential Manager / DPAPI for local secrets (planned)
- SQLite for local application data (planned)

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
- Do not store credentials in plaintext configuration files.
- Do not expose secrets in logs, diagnostics, or exception messages.
- Use least privilege. Never assume root access.
- Validate inputs and avoid constructing shell commands from untrusted strings.
- Require explicit confirmation for destructive or service-impacting actions.
- Report unavailable capabilities honestly; never simulate a successful operation.

## Project status

Initial repository scaffold. The UI shell and CI are being established before implementing the SSH transport and management modules.
