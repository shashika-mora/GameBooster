# Changelog

## 0.1.0 — development

- Fixed invalid WinUI symbols, duplicate startup code, and missing PRI resources in portable publishing.
- Added x64 and ARM64 CI publishing, locked dependency restores, startup smoke tests, and downloadable build artifacts.
- Fixed process-exit races, SQLite access across threads, false restoration success, shutdown restoration, preset mapping, and game selection refresh.
- Added real system CPU telemetry through Windows APIs, local rolling logs, and tests for recovery and session failures.

- Replaced the browser-backed prototype with a native WinUI 3 / C# Windows desktop application.
- Added native local SQLite persistence, Steam discovery, process launching, Windows power-plan restoration, recovery, and system monitoring.
- Added manual executable entry and basic local Steam manifest scanning.
- Added per-game power-plan profiles, persisted session snapshots, launch monitoring, and recovery on restart.
- Added CPU and RAM sampling, local session history, logging, and Windows CI build configuration.

Portable x64 startup and automated session tests are verified locally. A native installer, signing, ARM64 runtime testing, and acceptance testing with a real game's power-plan changes remain release work.
