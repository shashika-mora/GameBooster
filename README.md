# GameBooster

GameBooster is an offline-first Windows desktop utility for launching games with explicit, reversible session settings. It records the original system state before making a change, watches the launched game, and restores that state when the game exits. It makes no FPS promises and uploads no telemetry.

## Current scope

The first vertical slice provides a local game library, manual executable entry, basic Steam discovery, per-game power-plan selection, session launch and history, recovery of an interrupted power-plan change, and CPU/RAM monitoring. The default Balanced profile changes nothing. Performance selects the standard Windows High performance plan if installed; Custom accepts an existing scheme GUID. GameBooster never requires permanent elevation or disables security software.

Steam discovery reads local manifests; it may not identify an executable for every game. Those entries can be completed manually. GPU and FPS metrics are intentionally absent until a reliable local method is available.

## Architecture

- React and TypeScript render the UI and call narrow Tauri commands.
- Rust owns game discovery, process lifetime, SQLite persistence, monitoring, and all system changes.
- The SQLite database is stored in the operating system's application data directory and migrated on startup.
- Before a power-plan change, the original scheme and pending session are committed to disk. Startup exposes pending recovery instead of silently applying another profile.
- A failed restoration remains visible for retry.

## Development

Requires Node.js, npm, stable Rust with the Windows MSVC target, Microsoft C++ Build Tools, and WebView2. See the [Tauri Windows prerequisites](https://v2.tauri.app/start/prerequisites/). The development CI uses the standard MSVC toolchain.

```powershell
npm install
npm run tauri dev
```

Checks: `npm run build`, `cargo test --manifest-path src-tauri/Cargo.toml`. Installer: `npm run tauri build` on Windows.

## Safety and privacy

All data stays on the local machine. The app has no network feature or analytics. Power-plan selection is optional and requires an existing plan GUID. Recovery is offered on the next launch after a crash or restart. See [PRIVACY.md](PRIVACY.md).

Licensed under MIT.
