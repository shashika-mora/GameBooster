using Microsoft.Data.Sqlite;
using System.Globalization;

namespace GameBooster.Services;

public sealed class GameService : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly IPowerPlans _power;
    private readonly IGameLauncher _launcher;
    private readonly SystemMonitor _monitor = new();
    private readonly AppLog _log;
    private readonly CancellationTokenSource _shutdown = new();
    private IGameProcess? _running;
    private long? _activeSession;
    private Task _sessionTask = Task.CompletedTask;
    private bool _disposing;
    private bool _disposed;

    public static string DefaultDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameBooster");
    public string DataDirectory { get; }

    public GameService() : this(Path.Combine(DefaultDataDirectory, "gamebooster.db"), new WindowsPowerPlans(), new WindowsGameLauncher()) { }

    public GameService(string databasePath, IPowerPlans power, IGameLauncher launcher)
    {
        DataDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(DataDirectory);
        _power = power;
        _launcher = launcher;
        _log = new AppLog(DataDirectory);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        try { _db.Open(); Migrate(); }
        catch { _db.Dispose(); throw; }
        _log.Write("Application storage initialized.");
    }

    public bool IsRunning { get { lock (_gate) return _activeSession.HasValue; } }
    public Task WaitForSessionAsync() { lock (_gate) return _sessionTask; }

    private void Migrate()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if ((long)command.ExecuteScalar()! > 1)
            throw new InvalidOperationException("This database was created by a newer GameBooster version.");
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS games(id INTEGER PRIMARY KEY, name TEXT NOT NULL, executable TEXT UNIQUE, install_path TEXT, source TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles(game_id INTEGER PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE, preset TEXT NOT NULL DEFAULT 'Balanced', power_scheme TEXT);
            CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY, game_id INTEGER NOT NULL REFERENCES games(id), started_at TEXT NOT NULL, ended_at TEXT, status TEXT NOT NULL, previous_scheme TEXT, applied_scheme TEXT, restoration_result TEXT, profile_preset TEXT NOT NULL DEFAULT 'Balanced');
            CREATE TABLE IF NOT EXISTS operation_records(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id), operation TEXT NOT NULL, previous_value TEXT, requested_value TEXT, status TEXT NOT NULL, detail TEXT);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(sessions)";
        using (var reader = command.ExecuteReader())
        {
            var hasPreset = false;
            while (reader.Read()) hasPreset |= reader.GetString(1) == "profile_preset";
            reader.Close();
            if (!hasPreset)
            {
                command.CommandText = "ALTER TABLE sessions ADD COLUMN profile_preset TEXT NOT NULL DEFAULT 'Balanced'";
                command.ExecuteNonQuery();
            }
        }
        command.CommandText = "PRAGMA user_version=1";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Game> Games()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT id,name,executable,install_path,source FROM games ORDER BY name COLLATE NOCASE";
            using var reader = command.ExecuteReader();
            var result = new List<Game>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), NullableText(reader, 2), NullableText(reader, 3), reader.GetString(4)));
            return result;
        }
    }

    public long AddGame(string name, string executable)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Game name is required.");
        executable = ValidateExecutable(executable);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (Games().Any(game => string.Equals(game.Executable, executable, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This executable is already in your library.");
            using var transaction = _db.BeginTransaction();
            using var command = _db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO games(name,executable,install_path,source) VALUES($name,$exe,$path,'manual'); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$name", name.Trim());
            command.Parameters.AddWithValue("$exe", executable);
            command.Parameters.AddWithValue("$path", Path.GetDirectoryName(executable)!);
            var id = (long)command.ExecuteScalar()!;
            InsertDefaultProfile(id, transaction);
            transaction.Commit();
            return id;
        }
    }

    public void SetExecutable(long gameId, string executable)
    {
        executable = ValidateExecutable(executable);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE games SET executable=$exe WHERE id=$id";
            command.Parameters.AddWithValue("$exe", executable);
            command.Parameters.AddWithValue("$id", gameId);
            if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Game not found.");
        }
    }

    public int ScanSteam(IEnumerable<string>? roots = null)
    {
        var discovered = SteamDiscovery.Scan(roots ?? SteamDiscovery.DefaultRoots());
        lock (_gate)
        {
            ThrowIfDisposed();
            var inserted = 0;
            foreach (var game in discovered)
            {
                using var transaction = _db.BeginTransaction();
                using var command = _db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT COUNT(*) FROM games WHERE install_path=$path";
                command.Parameters.AddWithValue("$path", game.InstallPath);
                if ((long)command.ExecuteScalar()! > 0) continue;
                command.CommandText = "INSERT OR IGNORE INTO games(name,executable,install_path,source) VALUES($name,$exe,$path,'steam')";
                command.Parameters.AddWithValue("$name", game.Name);
                command.Parameters.AddWithValue("$exe", (object?)game.Executable ?? DBNull.Value);
                if (command.ExecuteNonQuery() == 0) continue;
                command.CommandText = "SELECT last_insert_rowid()";
                var id = (long)command.ExecuteScalar()!;
                InsertDefaultProfile(id, transaction);
                transaction.Commit();
                inserted++;
            }
            return inserted;
        }
    }

    private void InsertDefaultProfile(long gameId, SqliteTransaction transaction)
    {
        using var command = _db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO profiles(game_id,preset) VALUES($id,'Balanced')";
        command.Parameters.AddWithValue("$id", gameId);
        command.ExecuteNonQuery();
    }

    public Profile GetProfile(long gameId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT game_id,preset,power_scheme FROM profiles WHERE game_id=$id";
            command.Parameters.AddWithValue("$id", gameId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Profile not found.");
            return new(reader.GetInt64(0), reader.GetString(1), NullableText(reader, 2));
        }
    }

    public void SaveProfile(Profile profile)
    {
        profile = ProfileRules.Normalize(profile);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "INSERT INTO profiles(game_id,preset,power_scheme) VALUES($id,$preset,$scheme) ON CONFLICT(game_id) DO UPDATE SET preset=excluded.preset,power_scheme=excluded.power_scheme";
            command.Parameters.AddWithValue("$id", profile.GameId);
            command.Parameters.AddWithValue("$preset", profile.Preset);
            command.Parameters.AddWithValue("$scheme", (object?)profile.PowerScheme ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<Session> Sessions()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT s.id,s.game_id,g.name,s.profile_preset,s.started_at,s.ended_at,s.status,s.restoration_result FROM sessions s JOIN games g ON g.id=s.game_id ORDER BY s.id DESC LIMIT 100";
            using var reader = command.ExecuteReader();
            var result = new List<Session>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), ParseTime(reader.GetString(4)), reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)), reader.GetString(6), NullableText(reader, 7)));
            return result;
        }
    }

    public Recovery? Recovery()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT s.id,g.name,s.previous_scheme,s.applied_scheme,s.status FROM sessions s JOIN games g ON g.id=s.game_id WHERE s.status IN ('prepared','running','restore_failed') ORDER BY s.id DESC LIMIT 1";
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1), NullableText(reader, 2), NullableText(reader, 3), reader.GetString(4)) : null;
        }
    }

    public string ActivePowerScheme() => _power.ActiveScheme().ToString("D");

    public void Launch(long gameId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_activeSession.HasValue) throw new InvalidOperationException("A game session is already running.");
            if (Recovery() is not null) throw new InvalidOperationException("Recover the unfinished session before starting another game.");
            var game = Games().FirstOrDefault(game => game.Id == gameId) ?? throw new InvalidOperationException("Game not found.");
            var executable = ValidateExecutable(game.Executable ?? "");
            var profile = ProfileRules.Normalize(GetProfile(gameId));
            var previous = profile.PowerScheme is null ? null : ActivePowerScheme();
            var applied = profile.PowerScheme is not null && !profile.PowerScheme.Equals(previous, StringComparison.OrdinalIgnoreCase) ? profile.PowerScheme : null;
            var id = PrepareSession(gameId, profile.Preset, previous, applied);
            var failureStatus = "apply_failed";
            try
            {
                if (applied is not null) PowerRestoration.ApplyAndVerify(_power, Guid.Parse(applied));
                failureStatus = "launch_failed";
                _running = _launcher.Start(executable);
                _activeSession = id;
                try { UpdateStatus(id, "running", null, false); }
                catch (SqliteException exception) { _log.Write($"Session {id}: running status write failed: {exception.Message}"); }
                var process = _running;
                _sessionTask = Task.Run(() => ObserveExitAsync(id, process, previous, applied));
                _log.Write($"Session {id}: launched with {profile.Preset} profile.");
            }
            catch (Exception exception)
            {
                Finish(id, previous, applied, failureStatus);
                _log.Write($"Session {id}: {failureStatus}: {exception.Message}");
                throw;
            }
        }
    }

    private long PrepareSession(long gameId, string preset, string? previous, string? applied)
    {
        using var transaction = _db.BeginTransaction();
        using var command = _db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sessions(game_id,started_at,status,previous_scheme,applied_scheme,profile_preset) VALUES($game,$started,'prepared',$previous,$applied,$preset); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$game", gameId);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$previous", (object?)previous ?? DBNull.Value);
        command.Parameters.AddWithValue("$applied", (object?)applied ?? DBNull.Value);
        command.Parameters.AddWithValue("$preset", preset);
        var id = (long)command.ExecuteScalar()!;
        if (applied is not null)
        {
            command.CommandText = "INSERT INTO operation_records(session_id,operation,previous_value,requested_value,status) VALUES($session,'power_scheme',$previous,$applied,'prepared')";
            command.Parameters.AddWithValue("$session", id);
            command.ExecuteNonQuery();
        }
        // FULL synchronous commits the recovery snapshot before touching Windows.
        transaction.Commit();
        return id;
    }

    private async Task ObserveExitAsync(long id, IGameProcess process, string? previous, string? applied)
    {
        try
        {
            await process.WaitForExitAsync(_shutdown.Token).ConfigureAwait(false);
            lock (_gate)
                if (!_disposed && _activeSession == id) Finish(id, previous, applied, "completed");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _log.Write($"Session {id}: process observation failed: {exception.Message}");
                if (!_disposed)
                {
                    try { UpdateStatus(id, "restore_failed", "Process observation failed; review the game and recover the saved state.", false); }
                    catch (SqliteException databaseError) { _log.Write($"Session {id}: status write failed: {databaseError.Message}"); }
                }
            }
        }
    }

    private void Finish(long id, string? previous, string? applied, string status)
    {
        try
        {
            var result = PowerRestoration.Restore(_power, previous, applied);
            UpdateStatus(id, status, result, true);
            _log.Write($"Session {id}: {status}: {result}");
        }
        catch (Exception exception)
        {
            _log.Write($"Session {id}: restoration failed: {exception.Message}");
            try { UpdateStatus(id, "restore_failed", exception.Message, false); }
            catch (SqliteException databaseError) { _log.Write($"Session {id}: recovery status write failed: {databaseError.Message}"); }
        }
        finally
        {
            _running?.Dispose();
            _running = null;
            _activeSession = null;
        }
    }

    private void UpdateStatus(long id, string status, string? detail, bool finished)
    {
        using var transaction = _db.BeginTransaction();
        using var command = _db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE sessions SET status=$status,restoration_result=$detail,ended_at=$ended WHERE id=$id; UPDATE operation_records SET status=$status,detail=$detail WHERE session_id=$id";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$ended", finished ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Recover()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_activeSession.HasValue) throw new InvalidOperationException("The game session is still running.");
            var pending = Recovery() ?? throw new InvalidOperationException("No pending session.");
            try
            {
                var result = PowerRestoration.Restore(_power, pending.PreviousScheme, pending.AppliedScheme);
                UpdateStatus(pending.SessionId, "recovered", result, true);
                _log.Write($"Session {pending.SessionId}: recovered: {result}");
            }
            catch (Exception exception)
            {
                UpdateStatus(pending.SessionId, "restore_failed", exception.Message, false);
                throw;
            }
        }
    }

    public MonitorSample Sample() { lock (_gate) { ThrowIfDisposed(); return _monitor.Sample(); } }

    public void Dispose()
    {
        Task observer;
        Recovery? pendingAtClose;
        lock (_gate)
        {
            if (_disposed || _disposing) return;
            pendingAtClose = Recovery();
            _disposing = true;
            _shutdown.Cancel();
            observer = _sessionTask;
        }
        observer.GetAwaiter().GetResult();
        lock (_gate)
        {
            if (_activeSession.HasValue && pendingAtClose is { } pending)
                Finish(pending.SessionId, pending.PreviousScheme, pending.AppliedScheme, "interrupted");
            _disposed = true;
            _db.Dispose();
            _shutdown.Dispose();
            _log.Write("Application storage closed.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _disposing, this);
    private static string? NullableText(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string ValidateExecutable(string path)
    {
        path = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing .exe file.");
        return path;
    }
}
