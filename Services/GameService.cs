using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace GameBooster.Services;

public sealed class GameService : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private readonly IPowerPlans _power;
    private readonly IGameLauncher _launcher;
    private readonly IProcessAccess _processes;
    private readonly ISessionAwakeFactory _awakeFactory;
    private IDisposable? _awakeRequest;
    private string? _sessionWarning;
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

    public GameService(string databasePath, IPowerPlans power, IGameLauncher launcher, IProcessAccess? processes = null, ISessionAwakeFactory? awakeFactory = null)
    {
        DataDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        Directory.CreateDirectory(DataDirectory);
        _power = power;
        _launcher = launcher;
        _processes = processes ?? new WindowsProcessAccess();
        _awakeFactory = awakeFactory ?? new WindowsSessionAwakeFactory();
        _log = new AppLog(DataDirectory);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        try { _db.Open(); Migrate(); }
        catch { _db.Dispose(); throw; }
        _log.Write("Application storage initialized.");
    }

    public bool IsRunning { get { lock (_gate) return _activeSession.HasValue; } }
    public string? SessionWarning { get { lock (_gate) return _sessionWarning; } }
    public Task WaitForSessionAsync() { lock (_gate) return _sessionTask; }

    private void Migrate()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if ((long)command.ExecuteScalar()! > 2)
            throw new InvalidOperationException("This database was created by a newer GameBooster version.");
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS games(id INTEGER PRIMARY KEY, name TEXT NOT NULL, executable TEXT UNIQUE, install_path TEXT, source TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles(game_id INTEGER PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE, preset TEXT NOT NULL DEFAULT 'Balanced', power_scheme TEXT);
            CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY, game_id INTEGER NOT NULL REFERENCES games(id), started_at TEXT NOT NULL, ended_at TEXT, status TEXT NOT NULL, previous_scheme TEXT, applied_scheme TEXT, restoration_result TEXT, profile_preset TEXT NOT NULL DEFAULT 'Balanced');
            CREATE TABLE IF NOT EXISTS operation_records(id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL REFERENCES sessions(id), operation TEXT NOT NULL, previous_value TEXT, requested_value TEXT, status TEXT NOT NULL, detail TEXT);
            CREATE TABLE IF NOT EXISTS app_settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
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
        using var transaction = _db.BeginTransaction();
        EnsureColumn("profiles", "priority_preference", "TEXT NOT NULL DEFAULT 'Unchanged'", transaction);
        EnsureColumn("profiles", "keep_awake", "INTEGER NOT NULL DEFAULT 0", transaction);
        EnsureColumn("games", "is_archived", "INTEGER NOT NULL DEFAULT 0", transaction);
        EnsureColumn("operation_records", "snapshot_json", "TEXT", transaction);
        EnsureColumn("operation_records", "apply_status", "TEXT NOT NULL DEFAULT 'recorded'", transaction);
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version=2";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void EnsureColumn(string table, string column, string declaration, SqliteTransaction transaction)
    {
        using var command = _db.CreateCommand();
        command.Transaction = transaction;
        // All identifiers are internal constants, never user input.
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        var found = false;
        while (reader.Read()) found |= reader.GetString(1) == column;
        reader.Close();
        if (found) return;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        command.ExecuteNonQuery();
    }

    public AppSettings GetSettings()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT key,value FROM app_settings";
            using var reader = command.ExecuteReader();
            var values = new Dictionary<string, string>();
            while (reader.Read()) values[reader.GetString(0)] = reader.GetString(1);
            var theme = values.GetValueOrDefault("theme", "System");
            if (theme is not ("System" or "Light" or "Dark")) theme = "System";
            var seconds = int.TryParse(values.GetValueOrDefault("sampling_seconds"), out var value) && value is 3 or 5 or 10 ? value : 3;
            return new(theme, seconds);
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        if (settings.Theme is not ("System" or "Light" or "Dark") || settings.SamplingSeconds is not (3 or 5 or 10))
            throw new InvalidOperationException("Choose a supported appearance and refresh interval.");
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _db.BeginTransaction();
            using var command = _db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_settings(key,value) VALUES('theme',$theme),('sampling_seconds',$seconds) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$theme", settings.Theme);
            command.Parameters.AddWithValue("$seconds", settings.SamplingSeconds.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public IReadOnlyList<PowerPlanOption> PowerPlans() => _power.AvailableSchemes();

    public IReadOnlyList<OperationRecord> Operations(long sessionId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT id,operation,apply_status,status,detail FROM operation_records WHERE session_id=$session ORDER BY id";
            command.Parameters.AddWithValue("$session", sessionId);
            using var reader = command.ExecuteReader();
            var result = new List<OperationRecord>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableText(reader, 4)));
            return result;
        }
    }

    public IReadOnlyList<Game> Games()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT id,name,executable,install_path,source FROM games WHERE is_archived=0 ORDER BY name COLLATE NOCASE";
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
            using (var archived = _db.CreateCommand())
            {
                archived.CommandText = "SELECT id FROM games WHERE executable=$exe COLLATE NOCASE AND is_archived=1";
                archived.Parameters.AddWithValue("$exe", executable);
                if (archived.ExecuteScalar() is long existingId)
                {
                    archived.CommandText = "UPDATE games SET is_archived=0,name=$name WHERE id=$id";
                    archived.Parameters.AddWithValue("$name", name.Trim());
                    archived.Parameters.AddWithValue("$id", existingId);
                    archived.ExecuteNonQuery();
                    return existingId;
                }
            }
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

    public void RemoveGame(long gameId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var pending = Recovery();
            if (pending is not null && Sessions().Any(session => session.Id == pending.SessionId && session.GameId == gameId))
                throw new InvalidOperationException("Finish or recover this game's session before removing it.");
            using var command = _db.CreateCommand();
            command.CommandText = "UPDATE games SET is_archived=1 WHERE id=$id AND is_archived=0";
            command.Parameters.AddWithValue("$id", gameId);
            if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Game not found.");
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
            command.CommandText = "SELECT game_id,preset,power_scheme,priority_preference,keep_awake FROM profiles WHERE game_id=$id";
            command.Parameters.AddWithValue("$id", gameId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Profile not found.");
            return new(reader.GetInt64(0), reader.GetString(1), NullableText(reader, 2), reader.GetString(3), reader.GetBoolean(4));
        }
    }

    public void SaveProfile(Profile profile)
    {
        profile = ProfileRules.Normalize(profile);
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _db.CreateCommand();
            command.CommandText = "INSERT INTO profiles(game_id,preset,power_scheme,priority_preference,keep_awake) VALUES($id,$preset,$scheme,$priority,$awake) ON CONFLICT(game_id) DO UPDATE SET preset=excluded.preset,power_scheme=excluded.power_scheme,priority_preference=excluded.priority_preference,keep_awake=excluded.keep_awake";
            command.Parameters.AddWithValue("$id", profile.GameId);
            command.Parameters.AddWithValue("$preset", profile.Preset);
            command.Parameters.AddWithValue("$scheme", (object?)profile.PowerScheme ?? DBNull.Value);
            command.Parameters.AddWithValue("$priority", profile.PriorityPreference);
            command.Parameters.AddWithValue("$awake", profile.KeepAwake);
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
            _sessionWarning = null;
            var failureStatus = "apply_failed";
            try
            {
                if (applied is not null)
                {
                    PowerRestoration.ApplyAndVerify(_power, Guid.Parse(applied));
                    MarkOperation(id, "power_scheme", "applied", "Power plan change verified.", true);
                }
                failureStatus = "launch_failed";
                _running = _launcher.Start(executable);
                _activeSession = id;
                ApplySessionOptions(id, _running, profile);
                try { UpdateStatus(id, "running", null, false); }
                catch (SqliteException exception) { _log.Write($"Session {id}: running status write failed: {exception.Message}"); }
                var process = _running;
                _sessionTask = Task.Run(() => ObserveExitAsync(id, process, previous, applied));
                _log.Write($"Session {id}: launched with {profile.Preset} profile.");
            }
            catch (Exception exception)
            {
                if (failureStatus == "apply_failed" && applied is not null)
                {
                    try { MarkOperation(id, "power_scheme", "failed", exception.Message, true); }
                    catch (SqliteException databaseError) { _log.Write($"Session {id}: apply status write failed: {databaseError.Message}"); }
                }
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
            command.CommandText = "INSERT INTO operation_records(session_id,operation,previous_value,requested_value,status,apply_status) VALUES($session,'power_scheme',$previous,$applied,'prepared','prepared')";
            command.Parameters.AddWithValue("$session", id);
            command.ExecuteNonQuery();
        }
        // FULL synchronous commits the recovery snapshot before touching Windows.
        transaction.Commit();
        return id;
    }

    private void ApplySessionOptions(long id, IGameProcess process, Profile profile)
    {
        if (profile.PriorityPreference == "AboveNormal")
        {
            try
            {
                var identity = process.Identity;
                var state = _processes.Read(identity.Id);
                if (state is null || state.Identity != identity || state.Priority is GamePriority.AboveNormal or (int)System.Diagnostics.ProcessPriorityClass.High or (int)System.Diagnostics.ProcessPriorityClass.RealTime)
                {
                    RecordOperation(id, "process_priority", null, "skipped", "The game ended or already manages its own priority.");
                }
                else
                {
                    var snapshot = new PrioritySnapshot(identity, state.Priority, GamePriority.AboveNormal);
                    RecordOperation(id, "process_priority", JsonSerializer.Serialize(snapshot), "prepared", "Original game CPU priority saved.");
                    GamePriority.Apply(_processes, snapshot);
                    MarkOperation(id, "process_priority", "applied", "Above-normal game CPU priority verified.", true);
                }
            }
            catch (Exception exception) { OptionalOperationFailed(id, "process_priority", "Game CPU priority", exception); }
        }
        if (profile.KeepAwake)
        {
            try
            {
                RecordOperation(id, "keep_awake", null, "prepared", "Sleep prevention is limited to this session.");
                _awakeRequest = _awakeFactory.Create();
                MarkOperation(id, "keep_awake", "applied", "Idle-sleep prevention requested for this session.", true);
            }
            catch (Exception exception) { OptionalOperationFailed(id, "keep_awake", "Idle-sleep prevention", exception); }
        }
    }

    private void OptionalOperationFailed(long id, string operation, string label, Exception exception)
    {
        var message = $"{label} was unavailable: {exception.Message}";
        _sessionWarning = _sessionWarning is null ? message : _sessionWarning + " " + message;
        _log.Write($"Session {id}: {message}");
        try
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM operation_records WHERE session_id=$id AND operation=$operation";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$operation", operation);
            if ((long)command.ExecuteScalar()! == 0) RecordOperation(id, operation, null, "failed", exception.Message);
            else MarkOperation(id, operation, "failed", exception.Message, true);
        }
        catch (SqliteException databaseError) { _log.Write($"Session {id}: operation status write failed: {databaseError.Message}"); }
    }

    private void RecordOperation(long id, string operation, string? snapshot, string status, string detail)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "INSERT INTO operation_records(session_id,operation,status,apply_status,detail,snapshot_json) VALUES($id,$operation,$status,$status,$detail,$snapshot)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$snapshot", (object?)snapshot ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void MarkOperation(long id, string operation, string status, string detail, bool application = false)
    {
        using var command = _db.CreateCommand();
        command.CommandText = application
            ? "UPDATE operation_records SET status=$status,apply_status=$status,detail=$detail WHERE session_id=$id AND operation=$operation"
            : "UPDATE operation_records SET status=$status,detail=$detail WHERE session_id=$id AND operation=$operation";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$detail", detail);
        command.ExecuteNonQuery();
    }

    private string RestoreSession(long id, string? previous, string? applied)
    {
        var errors = new List<string>();
        var results = new List<string>();
        var snapshots = new List<string>();
        var hasAwakeRecord = false;
        try
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT snapshot_json FROM operation_records WHERE session_id=$id AND operation='process_priority' AND snapshot_json IS NOT NULL AND status!='restored'";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) snapshots.Add(reader.GetString(0));
            reader.Close();
            command.CommandText = "SELECT COUNT(*) FROM operation_records WHERE session_id=$id AND operation='keep_awake'";
            hasAwakeRecord = (long)command.ExecuteScalar()! > 0;
        }
        catch (SqliteException exception) { errors.Add($"Game priority recovery details could not be read: {exception.Message}"); }
        foreach (var json in snapshots)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<PrioritySnapshot>(json) ?? throw new InvalidOperationException("The game priority snapshot is invalid.");
                var result = GamePriority.Restore(_processes, snapshot);
                MarkOperation(id, "process_priority", "restored", result);
                results.Add(result);
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
                try { MarkOperation(id, "process_priority", "restore_failed", exception.Message); }
                catch (SqliteException databaseError) { _log.Write($"Session {id}: {databaseError.Message}"); }
            }
        }
        try
        {
            _awakeRequest?.Dispose();
            _awakeRequest = null;
            MarkOperation(id, "keep_awake", "restored", "The session's sleep-prevention request has been released.");
            if (hasAwakeRecord) results.Add("Idle-sleep prevention released.");
        }
        catch (Exception exception) { errors.Add(exception.Message); }
        try
        {
            var result = PowerRestoration.Restore(_power, previous, applied);
            MarkOperation(id, "power_scheme", "restored", result);
            results.Add(applied is null && (snapshots.Count > 0 || hasAwakeRecord) ? "Power plan was unchanged." : result);
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
            try { MarkOperation(id, "power_scheme", "restore_failed", exception.Message); }
            catch (SqliteException databaseError) { _log.Write($"Session {id}: {databaseError.Message}"); }
        }
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        return string.Join(" ", results);
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
                if (!_disposed && _activeSession == id)
                {
                    _sessionWarning = "Game tracking stopped early. Session settings were released or saved for recovery; your game can keep running.";
                    Finish(id, previous, applied, "interrupted");
                }
            }
        }
    }

    private void Finish(long id, string? previous, string? applied, string status)
    {
        try
        {
            var result = RestoreSession(id, previous, applied);
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
        command.CommandText = "UPDATE sessions SET status=$status,restoration_result=$detail,ended_at=$ended WHERE id=$id";
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
                var result = RestoreSession(pending.SessionId, pending.PreviousScheme, pending.AppliedScheme);
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
