# Gaming optimization decisions

Reviewed: 2026-11-07. These are implementation decisions and evidence boundaries for maintainers. Product screens describe the user's settings and their effects.

## Reference applications

The [Razer Cortex guide](https://dl.razerzone.com/master-guides/RazerCortex/v10.3/CortexOMG-ENG.pdf) presents a game library and an explicit boost/restore session flow. GameBooster uses that understandable lifecycle, with durable recovery and individual operation records.

[Process Lasso's documentation](https://bitsum.com/process-lasso-faq/) describes temporary power profiles and workload-dependent scheduling controls. Its [ProBalance documentation](https://bitsum.com/apps/process-lasso/docs/algorithms/probalance/) emphasizes conservative targeting and background responsiveness. GameBooster provides a bounded, opt-in game priority preference; it does not reproduce ProBalance's monitoring algorithm or claim equivalent results.

No comparative performance benchmark has been performed. These references inform feature design, not a claim that GameBooster outperforms another product.

## Automatic session operations

| Control | Basis and expected effect | Scope, default, and recovery |
| --- | --- | --- |
| Existing Windows power plan | Windows supports [enumerating installed schemes](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerenumerate) and selecting them. A performance plan changes power policy, with hardware-dependent power/heat tradeoffs. | Balanced leaves the current plan unchanged. Names are read from Windows. An explicitly selected plan is captured, applied, verified, and restored; external changes remain conflicts. |
| Above-normal game priority | [Windows scheduling priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities) influence CPU allocation under contention. This can affect background responsiveness and does not guarantee a frame-rate increase. | Off by default. Limited to the launched game, with no High or Real-time option. Existing high priorities are kept. The original class, PID, and start time are persisted before mutation. Recovery refuses to modify a reused PID or overwrite an external priority change. |
| Prevent automatic idle sleep | [PowerSetRequest](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest) can request display and system availability during inactivity. This supports uninterrupted sessions, rather than increasing FPS. | Off by default. A session-owned [power request](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powercreaterequest) is released at exit/shutdown and by Windows when its owning process closes. Manual sleep and platform battery policies still apply. |
| Lower monitoring frequency during play | Less frequent application work reduces GameBooster's own sampling and UI activity. | User-selectable 3, 5, or 10 seconds; gameplay uses at least 5 seconds. No performance-gain percentage is claimed. |

The application records optional-operation failures and continues observing the game. A failed restoration remains pending. All applicable restorations are attempted even when one fails.

## Windows-managed setup

The Gaming setup page opens an explicit allowlist of [documented Windows Settings pages](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings). The user reviews these settings in Windows; they are separate from automatic per-session changes.

- [Game Mode](https://support.xbox.com/en-US/help/games-apps/game-setup-and-play/use-game-mode-gaming-on-pc): open the Windows control. GameBooster does not infer its enabled state from undocumented registry defaults.
- [Windowed-game graphics optimizations](https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11): compatible DirectX 10/11 games on Windows 11 may benefit from reduced presentation latency. Hardware and per-game GPU preferences are reviewed on the same settings page.
- [Display refresh rate](https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-the-refresh-rate-on-your-monitor-in-windows): select a rate supported by the display and connection. Refresh rate and measured game FPS are distinct.
- Captures: review recording features when they are unnecessary for a session. No app-controlled global recording toggle is added.
- [Startup applications](https://support.microsoft.com/en-us/windows/experience/startup-boot/configure-startup-applications-in-windows): let the user choose unnecessary startup activity while preserving tools they rely on.
- [Recommended driver updates](https://support.microsoft.com/en-us/windows/hardware/drivers/automatically-get-recommended-and-updated-hardware-drivers): use Windows Update or the hardware manufacturer's official tooling.

## Validation and boundaries

Tests cover persisted settings, migration of older libraries, priority recovery after a simulated crash, PID reuse, permission denial, shutdown cleanup, restoration conflicts, archival/history preservation, and the Settings URI allowlist. Published startup checks render System, Light, and Dark themes and inspect resolved colors. Native process identity is tested without changing the host process priority.

State-changing failure scenarios use fake adapters. These tests do not establish a frame-rate improvement, real-game anti-cheat compatibility, battery endurance, thermal results, or ARM64 runtime behavior. Those require repeatable measurements on representative hardware.

Security services, Defender, the firewall, global Windows Update, registry performance recipes, memory purging, and automatic termination of background applications are outside these controls. Existing game-launcher handoff limitations remain visible in the project documentation.
