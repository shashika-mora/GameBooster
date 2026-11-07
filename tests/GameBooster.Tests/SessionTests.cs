using GameBooster.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GameBooster.Tests;

public sealed class SessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GameBooster-tests", Guid.NewGuid().ToString("N"));
    private readonly FakePower _power = new();
    private readonly FakeLauncher _launcher = new();
    private string Database => Path.Combine(_directory, "gamebooster.db");

    private GameService Create() => new(Database, _power, _launcher);
    private long AddGame(GameService service, string name = "Example")
    {
        Directory.CreateDirectory(_directory);
        var executable = Path.Combine(_directory, name + ".exe");
        File.WriteAllText(executable, "test fixture; fake launcher never executes this file");
        return service.AddGame(name, executable);
    }

    [Fact]
    public async Task ProcessExitRestoresSnapshotAndRecordsCompletedSession()
    {
        using var service = Create();
        var id = AddGame(service);
        service.SaveProfile(new(id, "Performance", null));
        _launcher.BeforeStart = () =>
        {
            using var db = new SqliteConnection($"Data Source={Database};Pooling=false");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT previous_scheme FROM sessions WHERE status='prepared'";
            Assert.Equal(FakePower.Original.ToString(), command.ExecuteScalar());
        };
        service.Launch(id);
        Assert.Equal(ProfileRules.HighPerformance, _power.Current);
        Assert.Throws<InvalidOperationException>(() => service.Recover());
        _launcher.Process.Exit();
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FakePower.Original, _power.Current);
        Assert.Null(service.Recovery());
        var session = Assert.Single(service.Sessions());
        Assert.Equal("completed", session.Status);
        Assert.Equal("Performance", session.ProfilePreset);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public async Task ImmediateExitStillCompletesAndDoesNotRaceRunningStatus()
    {
        using var service = Create();
        var id = AddGame(service);
        _launcher.Process.Exit();
        service.Launch(id);
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", Assert.Single(service.Sessions()).Status);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void FailedLaunchRestoresPowerPlan()
    {
        using var service = Create();
        var id = AddGame(service);
        service.SaveProfile(new(id, "Performance", null));
        _launcher.Fail = true;
        Assert.Throws<InvalidOperationException>(() => service.Launch(id));
        Assert.Equal(FakePower.Original, _power.Current);
        Assert.Equal("launch_failed", Assert.Single(service.Sessions()).Status);
    }

    [Fact]
    public async Task DatabaseReadsDuringExitRemainConsistent()
    {
        using var service = Create();
        var id = AddGame(service);
        service.Launch(id);
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.Single(service.Games());
                Assert.Single(service.Sessions());
                service.Recovery();
                Assert.Equal(id, service.GetProfile(id).GameId);
            }
        })).ToArray();
        _launcher.Process.Exit();
        await Task.WhenAll(readers.Append(service.WaitForSessionAsync())).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", Assert.Single(service.Sessions()).Status);
    }

    [Fact]
    public async Task RealShortLivedWindowsProcessIsObservedWithoutChangingPowerPlan()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var service = new GameService(Database, _power, new WindowsGameLauncher());
        var id = service.AddGame("Process test", Path.Combine(Environment.SystemDirectory, "where.exe"));
        service.Launch(id);
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", Assert.Single(service.Sessions()).Status);
        Assert.Equal(FakePower.Original, _power.Current);
    }

    [Fact]
    public async Task FailedRestorationSurvivesRestartAndCanBeRetried()
    {
        using (var service = Create())
        {
            var id = AddGame(service);
            service.SaveProfile(new(id, "Performance", null));
            service.Launch(id);
            _power.DenyChanges = true;
            _launcher.Process.Exit();
            await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("restore_failed", service.Recovery()!.Status);
        }
        _power.DenyChanges = false;
        using var reopened = Create();
        reopened.Recover();
        Assert.Equal(FakePower.Original, _power.Current);
        Assert.Equal("recovered", Assert.Single(reopened.Sessions()).Status);
        Assert.Null(reopened.Recovery());
    }

    [Fact]
    public async Task ExternalPlanChangeRemainsPendingUntilOriginalPlanIsSelected()
    {
        using var service = Create();
        var id = AddGame(service);
        service.SaveProfile(new(id, "Performance", null));
        service.Launch(id);
        var externalPlan = Guid.NewGuid();
        _power.Current = externalPlan;
        _launcher.Process.Exit();
        await service.WaitForSessionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(externalPlan, _power.Current);
        Assert.Equal("restore_failed", service.Recovery()!.Status);
        Assert.Throws<InvalidOperationException>(() => service.Recover());
        _power.Current = FakePower.Original;
        service.Recover();
        Assert.Null(service.Recovery());
    }

    [Fact]
    public void ClosingDuringSessionRestoresBeforeDisposingDatabase()
    {
        var service = Create();
        var id = AddGame(service);
        service.SaveProfile(new(id, "Performance", null));
        service.Launch(id);
        service.Dispose();
        Assert.Equal(FakePower.Original, _power.Current);
        using var reopened = Create();
        Assert.Equal("interrupted", Assert.Single(reopened.Sessions()).Status);
        Assert.Null(reopened.Recovery());
    }

    [Fact]
    public void PermissionDenialNeverLaunchesGame()
    {
        using var service = Create();
        var id = AddGame(service);
        service.SaveProfile(new(id, "Performance", null));
        _power.DenyChanges = true;
        Assert.Throws<UnauthorizedAccessException>(() => service.Launch(id));
        Assert.False(_launcher.Started);
        Assert.Equal("apply_failed", Assert.Single(service.Sessions()).Status);
        Assert.Null(service.Recovery());
    }

    [Fact]
    public void IgnoredSteamInsertDoesNotResetAnotherGamesProfile()
    {
        using var service = Create();
        var library = Path.Combine(_directory, "Steam");
        var install = Path.Combine(library, "steamapps", "common", "Example");
        Directory.CreateDirectory(install);
        // Manual entry has a different install directory, but the same executable.
        var bin = Path.Combine(install, "bin");
        Directory.CreateDirectory(bin);
        var executable = Path.Combine(install, "Example.exe");
        File.WriteAllText(executable, "fixture");
        var id = service.AddGame("Manual", executable);
        service.SaveProfile(new(id, "Performance", null));
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE games SET install_path=$path WHERE id=$id";
            command.Parameters.AddWithValue("$path", bin);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_1.acf"), "\"name\" \"Example\"\n\"installdir\" \"Example\"");
        Assert.Equal(0, service.ScanSteam([library]));
        Assert.Single(service.Games());
        Assert.Equal("Performance", service.GetProfile(id).Preset);
    }

    [Fact]
    public void PerformancePresetMapsToExplicitScheme()
    {
        var profile = ProfileRules.Normalize(new(1, "Performance", null));
        Assert.Equal(ProfileRules.HighPerformance.ToString(), profile.PowerScheme);
        Assert.Throws<InvalidOperationException>(() => ProfileRules.Normalize(new(1, "Balanced", profile.PowerScheme)));
        Assert.Throws<InvalidOperationException>(() => ProfileRules.Normalize(new(1, "Custom", "invalid")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSnapshotCanRecoverAfterCrashBeforeOrAfterApply(bool appliedBeforeCrash)
    {
        long gameId;
        using (var service = Create()) gameId = AddGame(service);
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=false"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO sessions(game_id,started_at,status,previous_scheme,applied_scheme,profile_preset) VALUES($id,$started,'prepared',$old,$new,'Performance')";
            command.Parameters.AddWithValue("$id", gameId);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$old", FakePower.Original.ToString());
            command.Parameters.AddWithValue("$new", ProfileRules.HighPerformance.ToString());
            command.ExecuteNonQuery();
        }
        if (appliedBeforeCrash) _power.Current = ProfileRules.HighPerformance;
        using var reopened = Create();
        Assert.Throws<InvalidOperationException>(() => reopened.Launch(gameId));
        reopened.Recover();
        Assert.Equal(FakePower.Original, _power.Current);
        Assert.Equal("recovered", Assert.Single(reopened.Sessions()).Status);
    }

    [Fact]
    public void MalformedRecoverySnapshotCannotClaimSuccess()
    {
        Assert.Throws<InvalidOperationException>(() => PowerRestoration.Restore(_power, null, ProfileRules.HighPerformance.ToString()));
        Assert.Throws<InvalidOperationException>(() => PowerRestoration.Restore(_power, "invalid", ProfileRules.HighPerformance.ToString()));
        Assert.Equal(FakePower.Original, _power.Current);
    }

    [Fact]
    public void NativeMonitorReturnsSystemCpuAndPhysicalMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        var monitor = new SystemMonitor();
        var sample = monitor.Sample();
        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.True(sample.TotalMemoryBytes > 0);
        Assert.InRange(sample.UsedMemoryBytes, 0UL, sample.TotalMemoryBytes);
    }

    [Fact]
    public void PowerChangeMustBeVerified()
    {
        _power.IgnoreChanges = true;
        Assert.Throws<InvalidOperationException>(() => PowerRestoration.ApplyAndVerify(_power, ProfileRules.HighPerformance));
    }

    [Fact]
    public void AmbiguousSteamExecutablesRemainUnset()
    {
        var install = Path.Combine(_directory, "steamapps", "common", "Unknown");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "first.exe"), "fixture");
        File.WriteAllText(Path.Combine(install, "second.exe"), "fixture");
        File.WriteAllText(Path.Combine(_directory, "steamapps", "appmanifest_2.acf"), "\"name\" \"Unknown\"\n\"installdir\" \"Unknown\"");
        Assert.Null(Assert.Single(SteamDiscovery.Scan([_directory])).Executable);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakePower : IPowerPlans
    {
        public static readonly Guid Original = new("381b4222-f694-41f0-9685-ff5bb260df2e");
        public Guid Current = Original;
        public bool DenyChanges;
        public bool IgnoreChanges;
        public Guid ActiveScheme() => Current;
        public void SetScheme(Guid scheme)
        {
            if (DenyChanges) throw new UnauthorizedAccessException("Permission denied");
            if (!IgnoreChanges) Current = scheme;
        }
    }

    private sealed class FakeLauncher : IGameLauncher
    {
        public readonly FakeProcess Process = new();
        public bool Fail;
        public bool Started;
        public Action? BeforeStart;
        public IGameProcess Start(string executable)
        {
            BeforeStart?.Invoke();
            if (Fail) throw new InvalidOperationException("Launch failed");
            Started = true;
            return Process;
        }
    }

    private sealed class FakeProcess : IGameProcess
    {
        public ProcessIdentity Identity => new(1234, 1000);
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public void Exit() => _exit.TrySetResult();
        public void Dispose() { }
    }
}
