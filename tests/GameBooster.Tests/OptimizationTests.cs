using System.Diagnostics;
using System.Text.Json;
using GameBooster.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameBooster.Tests;

public sealed class OptimizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameBooster-tests", Guid.NewGuid().ToString("N"));
    private readonly Power _power = new();
    private readonly Launcher _launcher = new();
    private readonly Processes _processes = new();
    private readonly Awake _awake = new();
    private string Database => Path.Combine(_directory, "gamebooster.db");
    private GameService Create() => new(Database, _power, _launcher, _processes, _awake);

    private long Add(GameService service)
    {
        var path = Path.Combine(_directory, "game.exe");
        File.WriteAllText(path, "fake executable; never executed");
        return service.AddGame("Test game", path);
    }

    [Fact]
    public void ThemeAndSamplingPersistAcrossRestart()
    {
        using (var service = Create())
        {
            Assert.Equal(new AppSettings(), service.GetSettings());
            service.SaveSettings(new("Light", 10));
        }
        using var reopened = Create();
        Assert.Equal(new AppSettings("Light", 10), reopened.GetSettings());
        Assert.Throws<InvalidOperationException>(() => reopened.SaveSettings(new("Unknown", 10)));
        Assert.Throws<InvalidOperationException>(() => reopened.SaveSettings(new("Dark", 0)));
    }

    [Fact]
    public void LegacyDatabaseMigratesWithoutLosingProfileOrRecovery()
    {
        Directory.CreateDirectory(_directory);
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE games(id INTEGER PRIMARY KEY,name TEXT NOT NULL,executable TEXT UNIQUE,install_path TEXT,source TEXT NOT NULL);
                CREATE TABLE profiles(game_id INTEGER PRIMARY KEY,preset TEXT NOT NULL,power_scheme TEXT);
                INSERT INTO games VALUES(1,'Legacy',NULL,NULL,'manual');
                INSERT INTO profiles VALUES(1,'Custom',NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        using var service = Create();
        Assert.Equal("Legacy", Assert.Single(service.Games()).Name);
        Assert.Equal(new Profile(1, "Custom", null), service.GetProfile(1));
        service.SaveSettings(new("Dark", 5));
        service.SaveProfile(new(1, "Custom", null, "AboveNormal", true));
        Assert.True(service.GetProfile(1).KeepAwake);
    }

    [Fact]
    public async Task PrioritySnapshotIsCommittedBeforeApplyAndRestoredAtExit()
    {
        using var service = Create();
        var id = Add(service);
        service.SaveProfile(new(id, "Custom", null, "AboveNormal", true));
        _processes.BeforeSet = () =>
        {
            using var db = new SqliteConnection($"Data Source={Database};Pooling=false");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT snapshot_json FROM operation_records WHERE operation='process_priority'";
            Assert.NotNull(JsonSerializer.Deserialize<PrioritySnapshot>((string)command.ExecuteScalar()!));
        };
        service.Launch(id);
        Assert.Equal(GamePriority.AboveNormal, _processes.Current!.Priority);
        Assert.True(_awake.Active);
        _launcher.Process.Exit();
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((int)ProcessPriorityClass.Normal, _processes.Current.Priority);
        Assert.False(_awake.Active);
        Assert.All(service.Operations(Assert.Single(service.Sessions()).Id), operation => Assert.Equal("restored", operation.Status));
    }

    [Fact]
    public void ClosingWhileGameRunsRestoresPriorityAndReleasesSleepRequest()
    {
        var service = Create();
        var id = Add(service);
        service.SaveProfile(new(id, "Custom", null, "AboveNormal", true));
        service.Launch(id);
        service.Dispose();
        Assert.Equal((int)ProcessPriorityClass.Normal, _processes.Current!.Priority);
        Assert.False(_awake.Active);
    }

    [Fact]
    public async Task OptionalPermissionDenialDoesNotPreventTheGameOrPowerRestoration()
    {
        using var service = Create();
        var id = Add(service);
        service.SaveProfile(new(id, "Performance", null, "AboveNormal", true));
        _processes.Deny = true;
        service.Launch(id);
        Assert.NotNull(service.SessionWarning);
        Assert.True(service.IsRunning);
        _launcher.Process.Exit();
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Power.Original, _power.Current);
        Assert.False(_awake.Active);
        Assert.Equal("completed", Assert.Single(service.Sessions()).Status);
        Assert.Equal("failed", service.Operations(1).Single(operation => operation.Operation == "process_priority").ApplyStatus);
    }

    [Fact]
    public async Task PriorityConflictStillRestoresPowerAndKeepsRecoveryPending()
    {
        using var service = Create();
        var id = Add(service);
        service.SaveProfile(new(id, "Performance", null, "AboveNormal", true));
        service.Launch(id);
        _processes.Current = _processes.Current! with { Priority = (int)ProcessPriorityClass.BelowNormal };
        _launcher.Process.Exit();
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Power.Original, _power.Current);
        Assert.False(_awake.Active);
        Assert.Equal("restore_failed", service.Recovery()!.Status);
        _processes.Current = _processes.Current with { Priority = (int)ProcessPriorityClass.Normal };
        service.Recover();
        Assert.Null(service.Recovery());
    }

    [Fact]
    public void ReusedPidAndExitedProcessesAreNeverModifiedDuringRecovery()
    {
        var snapshot = new PrioritySnapshot(_processes.Current!.Identity, (int)ProcessPriorityClass.Normal, GamePriority.AboveNormal);
        _processes.Current = new(new(snapshot.Identity.Id, snapshot.Identity.StartedUtcTicks + 1), GamePriority.AboveNormal);
        GamePriority.Restore(_processes, snapshot);
        Assert.Equal(0, _processes.Writes);
        _processes.Current = null;
        GamePriority.Restore(_processes, snapshot);
        Assert.Equal(0, _processes.Writes);
    }

    [Fact]
    public void PreparedPrioritySnapshotRecoversAfterAnApplicationCrash()
    {
        long gameId;
        using (var service = Create()) gameId = Add(service);
        var snapshot = new PrioritySnapshot(_processes.Current!.Identity, (int)ProcessPriorityClass.Normal, GamePriority.AboveNormal);
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO sessions(game_id,started_at,status,profile_preset) VALUES($game,$start,'running','Custom'); INSERT INTO operation_records(session_id,operation,status,apply_status,snapshot_json) VALUES(last_insert_rowid(),'process_priority','prepared','prepared',$snapshot)";
            command.Parameters.AddWithValue("$game", gameId);
            command.Parameters.AddWithValue("$start", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot));
            command.ExecuteNonQuery();
        }
        _processes.Current = _processes.Current with { Priority = GamePriority.AboveNormal };
        using var reopened = Create();
        reopened.Recover();
        Assert.Equal((int)ProcessPriorityClass.Normal, _processes.Current.Priority);
        Assert.Null(reopened.Recovery());
    }

    [Fact]
    public void UnsafePriorityValuesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => ProfileRules.Normalize(new(1, "Custom", null, "High")));
        Assert.Throws<InvalidOperationException>(() => GamePriority.Restore(_processes, new(_processes.Current!.Identity, (int)ProcessPriorityClass.RealTime, GamePriority.AboveNormal)));
    }

    [Fact]
    public async Task RemovingGameKeepsHistoryAndReaddingRestoresItsProfile()
    {
        using var service = Create();
        var id = Add(service);
        service.SaveProfile(new(id, "Custom", null, "AboveNormal"));
        service.Launch(id);
        Assert.Throws<InvalidOperationException>(() => service.RemoveGame(id));
        _launcher.Process.Exit();
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        service.RemoveGame(id);
        Assert.Empty(service.Games());
        Assert.Single(service.Sessions());
        var restoredId = service.AddGame("Restored game", Path.Combine(_directory, "game.exe"));
        Assert.Equal(id, restoredId);
        Assert.Equal("AboveNormal", service.GetProfile(id).PriorityPreference);
    }

    [Fact]
    public void WindowsSettingsLinksAreAnExplicitLocalAllowlist()
    {
        Assert.Equal(6, GamingGuidance.Cards.Count);
        foreach (var card in GamingGuidance.Cards)
        {
            Assert.Equal("ms-settings", GamingGuidance.SettingsUri(card.Key).Scheme);
            Assert.StartsWith("https://", card.SourceUrl);
        }
        Assert.Throws<InvalidOperationException>(() => GamingGuidance.SettingsUri("https://example.com"));
    }

    [Fact]
    public void NativeProcessIdentityGuardRejectsWrongStartTimeWithoutMutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var access = new WindowsProcessAccess();
        var state = access.Read(Environment.ProcessId);
        Assert.NotNull(state);
        Assert.True(state.Identity.StartedUtcTicks > 0);
        Assert.Throws<InvalidOperationException>(() => access.SetPriority(state.Identity with { StartedUtcTicks = state.Identity.StartedUtcTicks + 1 }, GamePriority.AboveNormal));
        Assert.Equal(state.Priority, access.Read(Environment.ProcessId)!.Priority);
    }

    [Fact]
    public async Task ObserverFailureRestoresSettingsWithoutTerminatingTheGame()
    {
        using var service = Create();
        var id = Add(service);
        service.SaveProfile(new(id, "Performance", null, "AboveNormal", true));
        _launcher.Process.FailObservation = true;
        service.Launch(id);
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Power.Original, _power.Current);
        Assert.Equal((int)ProcessPriorityClass.Normal, _processes.Current!.Priority);
        Assert.False(_awake.Active);
        Assert.False(service.IsRunning);
        Assert.Null(service.Recovery());
        Assert.Equal("interrupted", Assert.Single(service.Sessions()).Status);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }

    private sealed class Power : IPowerPlans
    {
        public static readonly Guid Original = new("381b4222-f694-41f0-9685-ff5bb260df2e");
        public Guid Current = Original;
        public Guid ActiveScheme() => Current;
        public void SetScheme(Guid scheme) => Current = scheme;
    }
    private sealed class Processes : IProcessAccess
    {
        public ProcessState? Current = new(new(42, 1000), (int)ProcessPriorityClass.Normal);
        public bool Deny;
        public int Writes;
        public Action? BeforeSet;
        public ProcessState? Read(int id) => Current;
        public void SetPriority(ProcessIdentity identity, int priority)
        {
            BeforeSet?.Invoke();
            if (Deny) throw new UnauthorizedAccessException("Permission denied");
            if (Current?.Identity != identity) throw new InvalidOperationException("Process ended");
            Writes++;
            Current = Current with { Priority = priority };
        }
    }
    private sealed class Launcher : IGameLauncher
    {
        public readonly GameProcess Process = new();
        public IGameProcess Start(string executable) => Process;
    }
    private sealed class GameProcess : IGameProcess
    {
        public bool FailObservation;
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProcessIdentity Identity => new(42, 1000);
        public Task WaitForExitAsync(CancellationToken token) => FailObservation ? Task.FromException(new InvalidOperationException("Observation failed")) : _exit.Task.WaitAsync(token);
        public void Exit() => _exit.TrySetResult();
        public void Dispose() { }
    }
    private sealed class Awake : ISessionAwakeFactory
    {
        public bool Active;
        public IDisposable Create() { Active = true; return new Request(this); }
        private sealed class Request(Awake owner) : IDisposable { public void Dispose() => owner.Active = false; }
    }
}
