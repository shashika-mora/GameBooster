# GameBooster

GameBooster is an offline-first Windows desktop utility for launching games with explicit, reversible session settings. It records the original system state before making a change, watches the launched game, and restores that state when the game exits. It makes no FPS promises and uploads no telemetry.

## Current scope

The first vertical slice provides a local game library, manual executable entry, basic Steam discovery, per-game power-plan selection, session launch and history, recovery of an interrupted power-plan change, and CPU/RAM monitoring. The default Balanced profile changes nothing. Performance selects the standard Windows High performance plan if installed; Custom accepts an existing scheme GUID. GameBooster never requires permanent elevation or disables security software.

Steam discovery reads local manifests; it may not identify an executable for every game. Those entries can be completed manually. GPU and FPS metrics are intentionally absent until a reliable local method is available.

## Architecture

- Native WinUI 3 and C# render the interface; there is no browser, WebView2, React, Vite, Electron, or Tauri runtime.
- The service layer owns Steam discovery, process lifetime, SQLite persistence, monitoring, and Windows power-plan changes.
- The SQLite database is stored in `%LOCALAPPDATA%\\GameBooster` and migrated on startup.
- Before a power-plan change, the original scheme and pending session are committed to disk. Startup exposes pending recovery instead of silently applying another profile.
- A failed restoration remains visible for retry.

## Development

Requires the .NET 8 SDK and the Windows App SDK/WinUI 3 workload in Visual Studio 2022. The project targets `net8.0-windows10.0.19041.0` for x64 and arm64.

```powershell
dotnet restore
dotnet run
```

Checks: `dotnet build --configuration Release`. Publish an MSIX package from Visual Studio's packaging project or with the Windows App SDK packaging tooling.

## Safety and privacy

All data stays on the local machine. The app has no network feature or analytics. Power-plan selection is optional and requires an existing plan GUID. Recovery is offered on the next launch after a crash or restart. See [PRIVACY.md](PRIVACY.md).

Licensed under MIT.
