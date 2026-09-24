using GameBooster.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace GameBooster;

public sealed partial class MainWindow : Window
{
    private readonly GameService _service = new();
    private readonly ObservableCollection<Game> _games = new();
    private readonly ObservableCollection<Session> _sessions = new();
    private DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Closed += (_, _) => _service.Dispose();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            var games = _service.Games(); _games.Clear(); foreach (var game in games) _games.Add(game);
            var sessions = _service.Sessions(); _sessions.Clear(); foreach (var session in sessions) _sessions.Add(session);
            GamesList.ItemsSource = _games; SessionsList.ItemsSource = _sessions; RecentGames.ItemsSource = _games.Take(4); RecentSessions.ItemsSource = _sessions.Take(4);
            HeroGames.Text = $"{_games.Count} games in your library";
            var recovery = _service.Recovery(); RecoveryPanel.Visibility = recovery is null ? Visibility.Collapsed : Visibility.Visible; if (recovery is not null) RecoveryText.Text = $"{recovery.GameName} was not fully restored. Recover the saved Windows power plan before launching another game.";
            StateText.Text = _service.IsRunning ? "●  Session running" : recovery is not null ? "●  Recovery needed" : "●  System ready"; HeroState.Text = _service.IsRunning ? "In session" : recovery is not null ? "Recovery needed" : "Standby";
            var sample = _service.Sample(); CpuText.Text = $"{sample.CpuPercent:0}%"; CpuBar.Value = sample.CpuPercent; MemoryText.Text = $"{sample.UsedMemoryBytes / 1073741824d:0.0} GB"; MemoryBar.Value = sample.TotalMemoryBytes == 0 ? 0 : sample.UsedMemoryBytes * 100d / sample.TotalMemoryBytes; MonitorCpu.Text = $"CPU usage: {sample.CpuPercent:0}%"; MonitorCpuBar.Value = sample.CpuPercent; MonitorMemory.Text = $"Memory: {sample.UsedMemoryBytes / 1073741824d:0.0} / {sample.TotalMemoryBytes / 1073741824d:0.0} GB"; MonitorMemoryBar.Value = MemoryBar.Value;
            var scheme = _service.ActivePowerScheme();
            PowerText.Text = scheme.Length >= 8 ? scheme[..8] : scheme;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "Dashboard"; PageTitle.Text = tag == "Monitor" ? "System Monitor" : tag;
        DashboardView.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed; GamesView.Visibility = tag == "Games" ? Visibility.Visible : Visibility.Collapsed; SessionsView.Visibility = tag == "Sessions" ? Visibility.Visible : Visibility.Collapsed; ProfilesView.Visibility = tag == "Profiles" ? Visibility.Visible : Visibility.Collapsed; MonitorView.Visibility = tag == "Monitor" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Profiles") { ProfileGame.ItemsSource = _games; if (_games.Count > 0) ProfileGame.SelectedIndex = 0; }
    }
    private void OpenGames_Click(object sender, RoutedEventArgs e) => Select("Games");
    private void OpenSessions_Click(object sender, RoutedEventArgs e) => Select("Sessions");
    private void Select(string tag) => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == tag);
    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (GamesList.SelectedItem is Game game) SelectedGameText.Text = $"{game.Name}\n{game.Executable ?? "Executable not set"}"; }
    private async void AddGame_Click(object sender, RoutedEventArgs e) { var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this); WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd); var file = await picker.PickSingleFileAsync(); if (file is null) return; var dialog = new ContentDialog { Title = "Game name", Content = new TextBox { PlaceholderText = "e.g. Portal 2" }, PrimaryButtonText = "Add", CloseButtonText = "Cancel", XamlRoot = Content.XamlRoot }; if (await dialog.ShowAsync() != ContentDialogResult.Primary) return; var name = ((TextBox)dialog.Content).Text; try { _service.AddGame(name, file.Path); Refresh(); ShowMessage("Game added to your library."); } catch (Exception ex) { ShowError(ex.Message); } }
    private void SetExecutable_Click(object sender, RoutedEventArgs e) { if (GamesList.SelectedItem is not Game game) { ShowError("Select a game first."); return; } _ = SetExecutableAsync(game); }
    private async Task SetExecutableAsync(Game game) { var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this)); var file = await picker.PickSingleFileAsync(); if (file is null) return; try { _service.SetExecutable(game.Id, file.Path); Refresh(); } catch (Exception ex) { ShowError(ex.Message); } }
    private void ScanSteam_Click(object sender, RoutedEventArgs e) { try { var count = _service.ScanSteam(); Refresh(); ShowMessage($"Steam scan complete. Added {count} game{(count == 1 ? "" : "s")}."); } catch (Exception ex) { ShowError(ex.Message); } }
    private void Launch_Click(object sender, RoutedEventArgs e) { if (GamesList.SelectedItem is not Game game) { ShowError("Select a game first."); return; } try { _service.Launch(game.Id); Refresh(); ShowMessage("Game launched. Restoration is armed."); } catch (Exception ex) { ShowError(ex.Message); } }
    private void Recover_Click(object sender, RoutedEventArgs e) { try { _service.Recover(); Refresh(); ShowMessage("Original power plan restored."); } catch (Exception ex) { ShowError(ex.Message); } }
    private void ProfileGame_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ProfileGame.SelectedItem is Game game) { var p = _service.GetProfile(game.Id); Preset.SelectedItem = Preset.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Content?.ToString() == p.Preset); Scheme.Text = p.PowerScheme ?? ""; } }
    private void SaveProfile_Click(object sender, RoutedEventArgs e) { if (ProfileGame.SelectedItem is not Game game || Preset.SelectedItem is not ComboBoxItem preset) return; try { _service.SaveProfile(new(game.Id, preset.Content.ToString()!, string.IsNullOrWhiteSpace(Scheme.Text) ? null : Scheme.Text.Trim())); ShowMessage("Profile saved."); } catch (Exception ex) { ShowError(ex.Message); } }
    private void Notice_Close(InfoBar sender, object args) => Notice.IsOpen = false;
    private void ShowMessage(string message) { Notice.Severity = InfoBarSeverity.Success; Notice.Message = message; Notice.IsOpen = true; }
    private void ShowError(string message) { Notice.Severity = InfoBarSeverity.Error; Notice.Message = message; Notice.IsOpen = true; }
}
