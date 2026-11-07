using GameBooster.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace GameBooster;

public sealed partial class MainWindow : Window
{
    private readonly GameService _service;
    private readonly ObservableCollection<Game> _games = new();
    private readonly ObservableCollection<Session> _sessions = new();
    private readonly ObservableCollection<PowerPlanOption> _plans = [new(null, "Keep current power plan")];
    private readonly DispatcherTimer _timer = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    private bool _captionHighContrast;
    private AppSettings _settings;
    private bool _busy;
    private bool _closed;
    private bool _loadingProfile;
    private bool _initializing = true;
    public string? LastError { get; private set; }

    public MainWindow(GameService service)
    {
        _service = service;
        _settings = service.GetSettings();
        InitializeComponent();
        Title = "GameBooster";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 880));
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        Root.Loaded += (_, _) => UpdateTitleBar();
        try { foreach (var plan in _service.PowerPlans()) _plans.Add(plan); }
        catch (Exception exception) { ShowError($"Power plans could not be listed: {exception.Message}"); }
        Scheme.ItemsSource = _plans;
        Scheme.SelectedIndex = 0;
        Priority.SelectedIndex = 0;
        PerformancePreset.IsEnabled = _plans.Any(plan => plan.Scheme == ProfileRules.HighPerformance.ToString("D"));
        SelectTag(ThemeSelector, _settings.Theme);
        SelectTag(SamplingSelector, _settings.SamplingSeconds.ToString());
        ProfileGame.ItemsSource = _games;
        SessionsList.ItemsSource = _sessions;
        GamingSetupList.ItemsSource = GamingGuidance.Cards;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Closed += (_, _) => { _closed = true; _timer.Stop(); _service.Dispose(); };
        _timer.Tick += (_, _) => { if (!_busy) Refresh(); };
        _initializing = false;
        Refresh();
        _timer.Start();
    }

    private static string? Tag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    public object AppearanceDiagnostics() => new
    {
        savedTheme = _settings.Theme,
        requestedTheme = Root.RequestedTheme.ToString(),
        actualTheme = Root.ActualTheme.ToString(),
        background = (Root.Background as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color.ToString(),
        text = (CpuText.Foreground as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color.ToString()
    };
    private static void SelectTag(ComboBox combo, string tag) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == tag);

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;
        _captionHighContrast = _accessibility.HighContrast;
        if (_captionHighContrast)
        {
            AppWindow.TitleBar.BackgroundColor = AppWindow.TitleBar.InactiveBackgroundColor = null;
            AppWindow.TitleBar.ForegroundColor = AppWindow.TitleBar.ButtonForegroundColor = null;
            AppWindow.TitleBar.ButtonBackgroundColor = AppWindow.TitleBar.ButtonInactiveBackgroundColor = null;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = AppWindow.TitleBar.ButtonHoverForegroundColor = null;
            AppWindow.TitleBar.ButtonHoverBackgroundColor = null;
            return;
        }
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var background = dark ? new Windows.UI.Color { A = 255, R = 16, G = 21, B = 29 } : new Windows.UI.Color { A = 255, R = 242, G = 244, B = 249 };
        AppWindow.TitleBar.BackgroundColor = background;
        AppWindow.TitleBar.InactiveBackgroundColor = background;
        AppWindow.TitleBar.ForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonBackgroundColor = background;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = background;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
        AppWindow.TitleBar.ButtonHoverForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = dark ? new Windows.UI.Color { A = 255, R = 52, G = 64, B = 82 } : new Windows.UI.Color { A = 255, R = 215, G = 221, B = 232 };
    }


    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || Tag(ThemeSelector) is not { } theme || !int.TryParse(Tag(SamplingSelector), out var interval)) return;
        try
        {
            var settings = new AppSettings(theme, interval);
            _service.SaveSettings(settings);
            _settings = settings;
            ApplyTheme();
            UpdateSamplingInterval();
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void UpdateSamplingInterval()
    {
        var seconds = _service.IsRunning ? Math.Max(5, _settings.SamplingSeconds) : _settings.SamplingSeconds;
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        SamplingText.Text = $"Local CPU and memory readings · every {seconds} seconds";
    }

    private void Refresh()
    {
        if (_closed) return;
        try
        {
            var games = _service.Games();
            if (!_games.SequenceEqual(games))
            {
                var profileId = (ProfileGame.SelectedItem as Game)?.Id;
                _games.Clear();
                foreach (var game in games) _games.Add(game);
                ApplyGameFilter();
                ProfileGame.SelectedItem = _games.FirstOrDefault(game => game.Id == profileId) ?? _games.FirstOrDefault();
                RecentGames.ItemsSource = _games.Take(4).ToArray();
            }
            else if (GamesList.ItemsSource is null) ApplyGameFilter();
            var sessions = _service.Sessions();
            if (!_sessions.SequenceEqual(sessions))
            {
                var sessionId = (SessionsList.SelectedItem as Session)?.Id;
                _sessions.Clear();
                foreach (var session in sessions) _sessions.Add(session);
                SessionsList.SelectedItem = _sessions.FirstOrDefault(session => session.Id == sessionId);
                RecentSessions.ItemsSource = _sessions.Take(4).ToArray();
            }
            EmptyDashboardGames.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyDashboardSessions.Visibility = EmptySessionsText.Visibility = _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveProfileButton.IsEnabled = ProfileGame.SelectedItem is Game && !_busy;
            HeroGames.Text = $"{_games.Count} {(_games.Count == 1 ? "game" : "games")} in your library";
            var pending = _service.Recovery();
            var running = _service.IsRunning;
            RecoveryPanel.Visibility = pending is not null && !running ? Visibility.Visible : Visibility.Collapsed;
            if (pending is not null) RecoveryText.Text = $"{pending.GameName}: review and restore the saved session settings before playing again.";
            StateText.Text = running ? "●  Session running" : pending is not null ? "●  Recovery needed" : "●  Ready to play";
            HeroState.Text = running ? "In session" : pending is not null ? "Recovery needed" : "Standby";
            ActiveSessionPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            var active = running ? sessions.FirstOrDefault(session => session.Id == pending?.SessionId) : null;
            if (active is not null)
            {
                ActiveSessionText.Text = active.GameName;
                ActiveSessionDetails.Text = $"{active.DurationLabel} · {active.ProfilePreset} profile. Original settings are saved for restoration.";
            }
            UpdateSelectedGame();
            UpdateSamplingInterval();
            // The WinRT high-contrast event is unavailable on some unpackaged hosts.
            // Theme resources update automatically; sample the title-bar preference.
            if (_accessibility.HighContrast != _captionHighContrast) UpdateTitleBar();
            var sample = _service.Sample();
            CpuText.Text = $"{sample.CpuPercent:0}%";
            CpuBar.Value = sample.CpuPercent;
            MemoryText.Text = $"{sample.UsedMemoryBytes / 1073741824d:0.0} GB";
            MemoryBar.Value = sample.TotalMemoryBytes == 0 ? 0 : sample.UsedMemoryBytes * 100d / sample.TotalMemoryBytes;
            MonitorCpu.Text = $"System CPU usage · {sample.CpuPercent:0}%";
            MonitorCpuBar.Value = sample.CpuPercent;
            MonitorMemory.Text = $"Memory · {sample.UsedMemoryBytes / 1073741824d:0.0} / {sample.TotalMemoryBytes / 1073741824d:0.0} GB";
            MonitorMemoryBar.Value = MemoryBar.Value;
            PowerText.Text = PlanName(_service.ActivePowerScheme());
            SettingsPower.Text = PowerText.Text;
            SettingsStorage.Text = _service.DataDirectory;
            if (SessionsList.SelectedItem is Session selectedSession) ShowSession(selectedSession);
            LastError = null;
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private string PlanName(string? scheme) => scheme is null ? "Keep current power plan" : _plans.FirstOrDefault(plan => string.Equals(plan.Scheme, scheme, StringComparison.OrdinalIgnoreCase))?.Name ?? "Unavailable power plan";

    private string ProfileSummary(Profile profile)
    {
        var priority = profile.PriorityPreference == "AboveNormal" ? "Above-normal game CPU priority" : "Default game CPU priority";
        return $"{profile.Preset} · {PlanName(profile.PowerScheme)} · {priority}" + (profile.KeepAwake ? " · Prevent idle sleep" : "");
    }

    private void ApplyGameFilter()
    {
        if (GameSearch is null || GamesList is null) return;
        var selectedId = (GamesList.SelectedItem as Game)?.Id;
        var search = GameSearch.Text.Trim();
        var visible = _games.Where(game => game.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        GamesList.ItemsSource = visible;
        GamesList.SelectedItem = visible.FirstOrDefault(game => game.Id == selectedId);
        EmptyLibraryText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyLibraryText.Text = _games.Count == 0 ? "Your library is empty. Add a game or scan your Steam libraries." : "No games match your search.";
    }

    private void GameSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyGameFilter();
    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_initializing) UpdateSelectedGame(); }

    private void UpdateSelectedGame()
    {
        if (GamesList.SelectedItem is Game game)
        {
            SelectedGameText.Text = $"{game.Name}\n{game.Executable ?? "Choose this game's executable to launch it."}";
            SelectedProfileSummary.Text = ProfileSummary(_service.GetProfile(game.Id));
        }
        else { SelectedGameText.Text = "Select a game from your library."; SelectedProfileSummary.Text = ""; }
        LaunchButton.IsEnabled = !_busy && !_service.IsRunning && _service.Recovery() is null && GamesList.SelectedItem is Game { Executable: not null };
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Dashboard";
        PageTitle.Text = tag switch { "Monitor" => "System monitor", "Setup" => "Gaming setup", _ => tag };
        DashboardView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        GamesView.Visibility = tag == "Games" ? Visibility.Visible : Visibility.Collapsed;
        SessionsView.Visibility = tag == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesView.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        MonitorView.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        SetupView.Visibility = tag == "Setup" ? Visibility.Visible : Visibility.Collapsed;
        MainScroller?.ChangeView(null, 0, null, true);
        if (!_initializing) Notice.IsOpen = false;
    }

    private void OpenGames_Click(object sender, RoutedEventArgs e) => Select("Games");
    private void OpenSessions_Click(object sender, RoutedEventArgs e) => Select("Sessions");
    private void OpenSetup_Click(object sender, RoutedEventArgs e) => Select("Setup");
    private void Select(string tag) => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag?.ToString() == tag);

    private void EditSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (GamesList.SelectedItem is not Game game) { ShowError("Select a game first."); return; }
        ProfileGame.SelectedItem = _games.FirstOrDefault(item => item.Id == game.Id);
        Select("Profiles");
    }

    private async void OpenWindowsSetting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: string key } && !await Windows.System.Launcher.LaunchUriAsync(GamingGuidance.SettingsUri(key)))
                ShowError("Windows could not open that setting. Open Windows Settings to review it.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task<Windows.Storage.StorageFile?> PickExecutableAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFileAsync();
    }

    private async void AddGame_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var file = await PickExecutableAsync();
            if (file is null) return;
            var name = new TextBox { Text = Path.GetFileNameWithoutExtension(file.Path), PlaceholderText = "Game name" };
            var dialog = new ContentDialog { Title = "Add to your library", Content = name, PrimaryButtonText = "Add game", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var gameName = name.Text;
            var executable = file.Path;
            long id = 0;
            await RunAsync(() => id = _service.AddGame(gameName, executable), "Game added to your library.");
            if (!_closed && id != 0) { GameSearch.Text = ""; GamesList.SelectedItem = _games.FirstOrDefault(game => game.Id == id); }
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || GamesList.SelectedItem is not Game game) return;
        var dialog = new ContentDialog { Title = $"Remove {game.Name}?", Content = "This removes the game from your library. Your game files and past sessions are kept.", PrimaryButtonText = "Remove from library", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await RunAsync(() => _service.RemoveGame(game.Id), "Game removed from your library. Past sessions were kept.");
    }

    private async void SetExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (GamesList.SelectedItem is not Game game) { ShowError("Select a game first."); return; }
        try
        {
            var file = await PickExecutableAsync();
            if (file is null) return;
            var executable = file.Path;
            await RunAsync(() => _service.SetExecutable(game.Id, executable), "Executable saved.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void ScanSteam_Click(object sender, RoutedEventArgs e)
    {
        var count = 0;
        await RunAsync(() => count = _service.ScanSteam(), () => $"Steam scan complete. Added {count} games.");
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (GamesList.SelectedItem is not Game game) { ShowError("Select a game first."); return; }
        await RunAsync(() => _service.Launch(game.Id), () => _service.SessionWarning is { } warning ? $"Game launched. {warning}" : "Game launched. Session restoration is ready.");
        if (!_closed && _service.SessionWarning is not null) Notice.Severity = InfoBarSeverity.Warning;
    }

    private async void Recover_Click(object sender, RoutedEventArgs e) => await RunAsync(_service.Recover, "Recovery completed and verified.");

    private void ProfileGame_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileGame.SelectedItem is not Game game || _initializing) return;
        try
        {
            var profile = _service.GetProfile(game.Id);
            _loadingProfile = true;
            SelectTag(Preset, profile.Preset);
            var plan = _plans.FirstOrDefault(item => string.Equals(item.Scheme, profile.PowerScheme, StringComparison.OrdinalIgnoreCase));
            if (plan is null) { plan = new(profile.PowerScheme, "Unavailable power plan — choose another"); _plans.Add(plan); }
            Scheme.SelectedItem = plan;
            Scheme.IsEnabled = profile.Preset == "Custom";
            SelectTag(Priority, profile.PriorityPreference);
            KeepAwake.IsChecked = profile.KeepAwake;
            SaveProfileButton.IsEnabled = !_busy;
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { _loadingProfile = false; }
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _loadingProfile || Scheme is null) return;
        var preset = Tag(Preset);
        Scheme.IsEnabled = preset == "Custom";
        _loadingProfile = true;
        if (preset == "Balanced") { Scheme.SelectedIndex = 0; SelectTag(Priority, "Unchanged"); KeepAwake.IsChecked = false; }
        if (preset == "Performance") Scheme.SelectedItem = _plans.FirstOrDefault(plan => plan.Scheme == ProfileRules.HighPerformance.ToString("D")) ?? _plans[0];
        _loadingProfile = false;
    }

    private void OptionalProfileSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _loadingProfile || KeepAwake is null) return;
        if (Tag(Preset) == "Balanced" && (Tag(Priority) == "AboveNormal" || KeepAwake.IsChecked == true))
        {
            _loadingProfile = true;
            SelectTag(Preset, "Custom");
            Scheme.IsEnabled = true;
            _loadingProfile = false;
        }
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGame.SelectedItem is not Game game || Tag(Preset) is not { } preset) return;
        var profile = new Profile(game.Id, preset, (Scheme.SelectedItem as PowerPlanOption)?.Scheme, Tag(Priority) ?? "Unchanged", KeepAwake.IsChecked == true);
        await RunAsync(() => _service.SaveProfile(profile), "Profile saved. These settings will be used next time you launch this game.");
    }

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && SessionsList.SelectedItem is Session session) ShowSession(session);
    }

    private void ShowSession(Session session)
    {
        SessionRestorationText.Text = session.RestorationResult ?? "Session settings are active. Restoration is pending.";
        OperationsList.ItemsSource = _service.Operations(session.Id);
    }

    private Task RunAsync(Action action, string message) => RunAsync(action, () => message);
    private async Task RunAsync(Action action, Func<string> message)
    {
        if (_busy || _closed) return;
        _busy = true;
        LaunchButton.IsEnabled = false;
        SaveProfileButton.IsEnabled = false;
        try { await Task.Run(action); if (!_closed) ShowMessage(message()); }
        catch (Exception exception) { if (!_closed) ShowError(exception.Message); }
        finally { _busy = false; Refresh(); }
    }

    private void Notice_Close(InfoBar sender, object args) => Notice.IsOpen = false;
    private void ShowMessage(string message) { Notice.Severity = InfoBarSeverity.Success; Notice.Message = message; Notice.IsOpen = true; }
    private void ShowError(string message) { LastError = message; Notice.Severity = InfoBarSeverity.Error; Notice.Message = message; Notice.IsOpen = true; }
}
