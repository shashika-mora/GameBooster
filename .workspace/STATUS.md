# Current Status

Updated: 2026-09-24

## Baseline

- Native WinUI 3 and C# application.
- No React, Vite, Tauri, Electron, or browser runtime.
- Local SQLite persistence under `%LOCALAPPDATA%\GameBooster`.
- Steam discovery, game launching, profiles, session history, recovery, power-plan restoration, and CPU/RAM monitoring are implemented.

## Validation

- XML project, application manifest, and XAML files parse successfully.
- `git diff --check` passes.
- Local `dotnet build` is currently unavailable when the .NET SDK is not installed.
- GitHub Actions uses `windows-latest` and runs the Release build.

## Recent security change

Game processes are created directly with `UseShellExecute = false` to avoid shell interpretation and reduce process-launch exposure. Do not revert this without a documented security review.
