# Changelog

## 0.1.0 — development

### 2026-11-07

- Added persistent System/Light/Dark appearance across the interface and theme-aware title bars.
- Replaced technical product copy and power-plan IDs with user-facing controls and Windows plan names.
- Added explicit opt-in game CPU priority and idle-sleep prevention, with operation records and crash/shutdown recovery.
- Added a researched Windows gaming setup checklist with local Settings shortcuts.
- Added library search, profile shortcuts, removal with history retained, active-session details, and per-operation history.
- Added database migration and priority safety tests, isolated UI preview data, and startup checks for all three appearance modes.

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
