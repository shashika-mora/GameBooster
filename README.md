# GameBooster

[![Windows build](https://github.com/shashika-mora/GameBooster/actions/workflows/build.yml/badge.svg)](https://github.com/shashika-mora/GameBooster/actions/workflows/build.yml)

GameBooster is an offline Windows desktop utility for launching games with explicit, reversible session settings. It saves the original power plan before making a change, watches the launched executable, and verifies restoration when the process exits. It makes no FPS improvement claims and uploads no telemetry.

## Development

Use Windows 10/11, a .NET SDK capable of building .NET 8, and network access for the initial NuGet restore. The WinUI compiler and Windows App SDK dependencies come from NuGet; Visual Studio is optional for command-line builds. The application runs offline after dependencies are installed or a portable build is extracted.

```powershell
dotnet restore GameBooster.csproj --locked-mode
dotnet run --project GameBooster.csproj
```

The default architecture is x64. Pass `-p:Platform=arm64` to build for ARM64. Run an ARM64 build on ARM64 Windows.

```powershell
dotnet build GameBooster.csproj -c Release --no-restore
dotnet restore tests/GameBooster.Tests/GameBooster.Tests.csproj --locked-mode
dotnet test tests/GameBooster.Tests/GameBooster.Tests.csproj -c Release --no-restore
```

## Portable builds

```powershell
dotnet publish GameBooster.csproj -c Release -p:Platform=x64 --self-contained true -o artifacts/win-x64
./scripts/SmokeTest.ps1 -AppPath artifacts/win-x64/GameBooster.exe
```

Keep the entire published folder together, including `GameBooster.pri` and the native runtime files. Launch `GameBooster.exe` from that folder. The .NET and Windows App SDK runtimes are included. This is an unpackaged folder distribution; an installer, signing, and MSIX publishing are future release work.

GitHub Actions builds and publishes x64 and ARM64 artifacts. The x64 job also runs the tests and starts the published application with isolated temporary data to verify that its WinUI window loads. ARM64 is compiled on the x64 runner and needs runtime validation on ARM64 hardware. Failed builds retain diagnostics; successful builds upload the portable application folders.

## Current features

- Manually add a game with a native executable picker, or discover games from local Steam manifests and library locations.
- Save a per-game profile and see its exact power-plan setting.
- Persist a recovery snapshot and operation record before changing Windows.
- Observe process exit, restore and verify the original plan, and save session history.
- Recover unfinished sessions after a crash or restart. Failed recovery stays visible and can be retried.
- Sample total system CPU usage and physical memory every three seconds.
- View local storage information, recovery guidance, and session restoration results.

| Preset | Explicit operation |
| --- | --- |
| Balanced | No power-plan change. |
| Performance | Select the existing Windows High performance scheme, `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c`. If unavailable or denied, the game is not launched. |
| Custom | Select the existing scheme GUID entered by the user, or leave it blank for no change. |

Steam discovery leaves ambiguous executable paths unset so the user can choose the correct file. Games that start a separate launcher may end their monitored session early. GPU, temperature, and FPS measurement are not implemented.

## Safety and architecture

The application uses native WinUI 3 with C# services and SQLite. Windows power-plan APIs, system telemetry, and process launching are separated from the interface. Direct process creation uses `UseShellExecute=false`; the application runs as the current user.

SQLite commits the active snapshot before applying a profile. Database access is serialized between the UI and process observer. Closing the app cancels observation and attempts restoration before disposing storage, while leaving the game running. A later launch can recover any unfinished snapshot. A second app instance cannot start against the same data directory.

If another application or the user changes the power plan during a session, GameBooster records a conflict. Select the original plan in Windows and retry recovery; GameBooster verifies it before clearing the pending session.

Tests use temporary SQLite databases and fake system adapters to cover process-exit races, launch and permission failures, restoration verification, external plan changes, recovery across restarts, and shutdown. A Windows test also observes a real short-lived utility process without changing the machine's power plan. Applying and restoring a real gaming profile still requires acceptance testing on the target machine.

Data is stored in `%LOCALAPPDATA%\GameBooster`. Logs rotate locally at approximately 1 MB and retain one previous file. See [PRIVACY.md](PRIVACY.md). Licensed under MIT.
