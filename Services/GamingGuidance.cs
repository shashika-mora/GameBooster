namespace GameBooster.Services;

public sealed record GamingSetupCard(string Key, string Title, string Description, string SettingsUri, string SourceUrl);

public static class GamingGuidance
{
    public static IReadOnlyList<GamingSetupCard> Cards { get; } = Array.AsReadOnly(new[]
    {
        new GamingSetupCard("game-mode", "Game Mode", "Review Game Mode so Windows can prioritize your gaming session.", "ms-settings:gaming-gamemode", "https://support.xbox.com/en-US/help/games-apps/game-setup-and-play/use-game-mode-gaming-on-pc"),
        new GamingSetupCard("graphics", "Choose your GPU and windowed-game settings", "Select a preferred GPU for your game. On supported Windows 11 systems, windowed-game optimizations can reduce presentation latency for compatible DirectX 10 and 11 games.", "ms-settings:display-advancedgraphics", "https://support.microsoft.com/en-us/windows/hardware/display-graphics/optimizations-for-windowed-games-in-windows-11"),
        new GamingSetupCard("display", "Check your display refresh rate", "Choose a refresh rate supported by your monitor and connection. A higher refresh rate can make motion look smoother; it does not create extra rendered frames.", "ms-settings:display-advanced", "https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-the-refresh-rate-on-your-monitor-in-windows"),
        new GamingSetupCard("captures", "Review background recording", "If you do not use game captures, review background recording to avoid recording work you do not need. Keep it enabled when saving recent gameplay matters to you.", "ms-settings:gaming-gamedvr", "https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings"),
        new GamingSetupCard("startup", "Keep startup apps intentional", "Review apps that run when you sign in. Reducing unnecessary startup activity can leave more resources available. Leave launchers, voice chat, and security tools you rely on enabled.", "ms-settings:startupapps", "https://support.microsoft.com/en-us/windows/experience/startup-boot/configure-startup-applications-in-windows"),
        new GamingSetupCard("updates", "Keep Windows and graphics drivers current", "Review available updates before you start a long session. Use Windows Update or your graphics hardware manufacturer's official tools for driver updates.", "ms-settings:windowsupdate", "https://support.microsoft.com/en-us/windows/hardware/drivers/automatically-get-recommended-and-updated-hardware-drivers")
    });

    public static Uri SettingsUri(string key) => new(Cards.FirstOrDefault(card => card.Key == key)?.SettingsUri
        ?? throw new InvalidOperationException("This gaming setting is not available."));
}
