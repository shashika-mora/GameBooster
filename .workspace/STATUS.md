# Current Status

Updated: 2026-11-03

## Baseline

- Native WinUI 3 and C# targeting .NET 8, with SQLite under `%LOCALAPPDATA%\GameBooster`.
- Generated WinUI startup is the only application entry point. Navigation uses valid WinUI symbols.
- Power-plan changes use Windows APIs and require verification. Pending snapshots and operation records are committed before changes.
- UI and process-observer database access is serialized. Closing the app restores an active session before disposing storage.
- System CPU sampling uses GetSystemTimes; RAM uses GlobalMemoryStatusEx.
- Refresh preserves game selection and profile edits. Windows startup includes XamlControlsResources.

## Verification

- The session, persistence, recovery, Steam, and Windows adapter tests run with `dotnet test tests/GameBooster.Tests/GameBooster.Tests.csproj`.
- Portable x64 startup is tested through `scripts/SmokeTest.ps1` with an isolated data directory.
- The published application must include its application PRI resource and all runtime files. EnableMsixTooling is required for the resource to be published even though WindowsPackageType is None.
- GitHub Actions restores locked dependencies, builds/publishes x64 and ARM64, runs tests and x64 startup, and uploads app artifacts and diagnostics. Live results: https://github.com/shashika-mora/GameBooster/actions/workflows/build.yml
- Local validation includes x64 window startup and navigation, x64/ARM64 publishing, and the automated test suite. ARM64 runtime behavior and a real game's power-plan changes still need acceptance testing.

## Development boundaries

- Process launching must keep UseShellExecute=false.
- Never suppress a failed restoration or clear pending recovery without verification.
- Tests must use temporary databases and fake power-plan adapters for state-changing scenarios.
- The old src-tauri directory and workspace ZIP are local ignored leftovers; do not add them to commits.
