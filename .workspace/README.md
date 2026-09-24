# Collaboration Workspace

This directory contains repository-local operating notes for maintainers and coding agents. It is intentionally small, text-only, and free of credentials, user data, build output, or generated binaries.

## Working rules

1. Read this file and `STATUS.md` before changing code.
2. Inspect the current branch and working tree before editing.
3. Keep changes focused and preserve existing Windows behavior unless the task explicitly changes it.
4. Run the narrowest relevant validation. If a required tool is unavailable, record that fact rather than claiming a successful build.
5. Update `STATUS.md` after meaningful work. Add dated entries to `activity.log`.
6. Do not store secrets, personal paths, database files, crash dumps, or generated output here.
7. Commit coherent changes with a clear message. Do not rewrite unrelated commits.

## Build and run

The application is a native WinUI 3 project targeting .NET 8 and Windows 10 build 19041 or later.

```powershell
dotnet restore
dotnet build --configuration Release --arch x64
dotnet run --framework net8.0-windows10.0.19041.0
```

Visual Studio 2022 must have the Windows App SDK / WinUI 3 workload installed. The repository's GitHub Actions workflow builds on `windows-latest`.

## Security checks

- Game processes are started directly with `UseShellExecute = false`.
- User-selected executable paths must exist and use the `.exe` extension.
- SQL statements use parameters.
- The app does not request administrator privileges or transmit telemetry.
- Review process creation, power-plan commands, file paths, and database writes when modifying the service layer.
