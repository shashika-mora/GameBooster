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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _busy;
    private bool _closed;
    private bool _loadingProfile;
    public string? LastError { get; private set; }

    public MainWindow(GameService service)
    {
        _service = service;
        InitializeComponent();
        Title = "GameBooster";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
        GamesList.ItemsSource = _games;
        ProfileGame.ItemsSource = _games;
        SessionsList.ItemsSource = _sessions;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Closed += (_, _) => { _closed = true; _timer.Stop(); _service.Dispose(); };
        _timer.Tick += (_, _) => { if (!_busy) Refresh(); };
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        if (_closed) return;
        try
        {
            var games = _service.Games();
            if (!_games.SequenceEqual(games))
            {
                var gameId = (GamesList.SelectedItem as Game)?.Id;
                var profileId = (ProfileGame.SelectedItem as Game)?.Id;
                _games.Clear();
                foreach (var game in games) _games.Add(game);
                GamesList.SelectedItem = _games.FirstOrDefault(game => game.Id == gameId);
                ProfileGame.SelectedItem = _games.FirstOrDefault(game => game.Id == profileId) ?? _games.FirstOrDefault();
                RecentGames.ItemsSource = _games.Take(4).ToArray();
            }
            var sessions = _service.Sessions();
            if (!_sessions.SequenceEqual(sessions))
            {
                _sessions.Clear();
                foreach (var session in sessions) _sessions.Add(session);
                RecentSessions.ItemsSource = _sessions.Take(4).ToArray();
            }
            HeroGames.Text = $"{_games.Count} games in your library";
            var pending = _service.Recovery();
            var running = _service.IsRunning;
            RecoveryPanel.Visibility = pending is not null && !running ? Visibility.Visible : Visibility.Collapsed;
            if (pending is not null) RecoveryText.Text = $"{pending.GameName}: review and restore the saved power plan before starting another session.";
            StateText.Text = running ? "●  Session running" : pending is not null ? "●  Recovery needed" : "●  System ready";
            HeroState.Text = running ? "In session" : pending is not null ? "Recovery needed" : "Standby";
            LaunchButton.IsEnabled = !_busy && !running && pending is null && GamesList.SelectedItem is Game { Executable: not null };
            var sample = _service.Sample();
            CpuText.Text = $"{sample.CpuPercent:0}%";
            CpuBar.Value = sample.CpuPercent;
            MemoryText.Text = $"{sample.UsedMemoryBytes / 1073741824d:0.0} GB";
            MemoryBar.Value = sample.TotalMemoryBytes == 0 ? 0 : sample.UsedMemoryBytes * 100d / sample.TotalMemoryBytes;
            MonitorCpu.Text = $"System CPU usage: {sample.CpuPercent:0}%";
            MonitorCpuBar.Value = sample.CpuPercent;
            MonitorMemory.Text = $"Physical memory: {sample.UsedMemoryBytes / 1073741824d:0.0} / {sample.TotalMemoryBytes / 1073741824d:0.0} GB";
            MonitorMemoryBar.Value = MemoryBar.Value;
            var scheme = _service.ActivePowerScheme();
            PowerText.Text = scheme[..8];
            ToolTipService.SetToolTip(PowerText, scheme);
            SettingsPower.Text = scheme;
            SettingsStorage.Text = _service.DataDirectory;
            LastError = null;
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Dashboard";
        PageTitle.Text = tag == "Monitor" ? "System Monitor" : tag;
        DashboardView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        GamesView.Visibility = tag == "Games" ? Visibility.Visible : Visibility.Collapsed;
        SessionsView.Visibility = tag == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesView.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
        MonitorView.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenGames_Click(object sender, RoutedEventArgs e) => Select("Games");
    private void OpenSessions_Click(object sender, RoutedEventArgs e) => Select("Sessions");
    private void Select(string tag) => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag?.ToString() == tag);

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedGameText.Text = GamesList.SelectedItem is Game game ? $"{game.Name}\n{game.Executable ?? "Executable not set"}" : "Select a game from your library.";
        if (LaunchButton is not null) LaunchButton.IsEnabled = !_busy && !_service.IsRunning && _service.Recovery() is null && GamesList.SelectedItem is Game { Executable: not null };
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
            var dialog = new ContentDialog { Title = "Game name", Content = name, PrimaryButtonText = "Add", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var gameName = name.Text;
            var executable = file.Path;
            await RunAsync(() => _service.AddGame(gameName, executable), "Game added to your library.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async void SetExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (GamesList.SelectedItem is not Game game) { ShowError("Select a game first."); return; }
        try
        {
            var file = await PickExecutableAsync();
            if (file is not null)
            {
                var executable = file.Path;
                await RunAsync(() => _service.SetExecutable(game.Id, executable), "Executable saved.");
            }
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
        await RunAsync(() => _service.Launch(game.Id), "Game launched. Restoration is armed.");
    }

    private async void Recover_Click(object sender, RoutedEventArgs e) => await RunAsync(_service.Recover, "Recovery completed and verified.");

    private void ProfileGame_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileGame.SelectedItem is not Game game) return;
        try
        {
            var profile = _service.GetProfile(game.Id);
            _loadingProfile = true;
            Preset.SelectedItem = Preset.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == profile.Preset);
            Scheme.Text = profile.PowerScheme ?? "";
            Scheme.IsEnabled = profile.Preset == "Custom";
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { _loadingProfile = false; }
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || Scheme is null || Preset.SelectedItem is not ComboBoxItem item) return;
        var preset = item.Content?.ToString();
        Scheme.IsEnabled = preset == "Custom";
        if (preset == "Balanced") Scheme.Text = "";
        if (preset == "Performance") Scheme.Text = ProfileRules.HighPerformance.ToString("D");
    }

    private async void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileGame.SelectedItem is not Game game || Preset.SelectedItem is not ComboBoxItem preset) return;
        var profile = new Profile(game.Id, preset.Content.ToString()!, string.IsNullOrWhiteSpace(Scheme.Text) ? null : Scheme.Text.Trim());
        await RunAsync(() => _service.SaveProfile(profile), "Profile saved.");
    }

    private Task RunAsync(Action action, string message) => RunAsync(action, () => message);
    private async Task RunAsync(Action action, Func<string> message)
    {
        if (_busy || _closed) return;
        _busy = true;
        try { await Task.Run(action); if (!_closed) ShowMessage(message()); }
        catch (Exception exception) { if (!_closed) ShowError(exception.Message); }
        finally { _busy = false; Refresh(); }
    }

    private void Notice_Close(InfoBar sender, object args) => Notice.IsOpen = false;
    private void ShowMessage(string message) { Notice.Severity = InfoBarSeverity.Success; Notice.Message = message; Notice.IsOpen = true; }
    private void ShowError(string message) { LastError = message; Notice.Severity = InfoBarSeverity.Error; Notice.Message = message; Notice.IsOpen = true; }
}
