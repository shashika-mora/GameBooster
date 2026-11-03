using GameBooster.Services;
using Microsoft.UI.Xaml;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameBooster;

public partial class App : Application
{
    private MainWindow? _window;
    private Mutex? _instance;
    private AppLog _log = new(GameService.DefaultDataDirectory);

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => _log.Write($"Unhandled application error: {args.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var smokeTest = arguments.Contains("--smoke-test");
        string? Option(string key) => Array.IndexOf(arguments, key) is var index && index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        var directory = smokeTest ? Option("--data-dir") ?? throw new ArgumentException("Smoke tests require --data-dir.") : GameService.DefaultDataDirectory;
        _log = new AppLog(directory);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
        _instance = new Mutex(true, @"Local\GameBooster-" + identity, out var created);
        if (!created) { _instance.Dispose(); Exit(); return; }
        GameService? service = null;
        try
        {
            service = new GameService(Path.Combine(directory, "gamebooster.db"), new WindowsPowerPlans(), new WindowsGameLauncher());
            _window = new MainWindow(service);
            _window.Closed += (_, _) => { _instance.ReleaseMutex(); _instance.Dispose(); };
            if (smokeTest)
            {
                var report = Option("--report") ?? throw new ArgumentException("Smoke tests require --report.");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    var error = _window.LastError;
                    var loaded = ((FrameworkElement)_window.Content).IsLoaded;
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                    File.WriteAllText(report, JsonSerializer.Serialize(new { success = loaded && error is null, windowLoaded = loaded, error, gameCount = service.Games().Count, powerScheme = service.ActivePowerScheme() }));
                    if (!loaded || error is not null) Environment.ExitCode = 1;
                    _window.Close();
                };
                ((FrameworkElement)_window.Content).Loaded += (_, _) => timer.Start();
            }
            _window.Activate();
        }
        catch (Exception exception)
        {
            _log.Write($"Startup failed: {exception}");
            if (smokeTest && Option("--report") is { } report)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, JsonSerializer.Serialize(new { success = false, windowLoaded = false, error = exception.ToString() }));
            }
            service?.Dispose();
            throw;
        }
    }
}
