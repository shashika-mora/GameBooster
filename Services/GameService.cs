using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace GameBooster.Services;

public sealed class GameService : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private Process? _running;
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt;

    public GameService()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameBooster");
        Directory.CreateDirectory(folder);
        _db = new SqliteConnection($"Data Source={Path.Combine(folder, "gamebooster.db")}");
        _db.Open();
        Migrate();
    }

    public bool IsRunning => _running is { HasExited: false };

    private void Migrate()
    {
        using var command = _db.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS games(id INTEGER PRIMARY KEY, name TEXT NOT NULL, executable TEXT UNIQUE, install_path TEXT, source TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles(game_id INTEGER PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE, preset TEXT NOT NULL DEFAULT 'Balanced', power_scheme TEXT);
            CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY, game_id INTEGER NOT NULL REFERENCES games(id), started_at TEXT NOT NULL, ended_at TEXT, status TEXT NOT NULL, previous_scheme TEXT, applied_scheme TEXT, restoration_result TEXT, profile_preset TEXT NOT NULL DEFAULT 'Balanced');
            """;
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Game> Games()
    {
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT id,name,executable,install_path,source FROM games ORDER BY name COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var result = new List<Game>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        return result;
    }

    public long AddGame(string name, string executable)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Game name is required.");
        if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing .exe file.");
        using var command = _db.CreateCommand();
        command.CommandText = "INSERT INTO games(name,executable,install_path,source) VALUES($name,$exe,$path,'manual'); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$exe", executable);
        command.Parameters.AddWithValue("$path", Path.GetDirectoryName(executable) ?? "");
        var id = (long)command.ExecuteScalar()!;
        SaveProfile(new(id, "Balanced", null));
        return id;
    }

    public void SetExecutable(long gameId, string executable)
    {
        if (!File.Exists(executable) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose an existing .exe file.");
        using var command = _db.CreateCommand();
        command.CommandText = "UPDATE games SET executable=$exe,install_path=$path WHERE id=$id";
        command.Parameters.AddWithValue("$exe", executable);
        command.Parameters.AddWithValue("$path", Path.GetDirectoryName(executable) ?? "");
        command.Parameters.AddWithValue("$id", gameId);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException("Game not found.");
    }

    public int ScanSteam()
    {
        var roots = new[] { Environment.GetEnvironmentVariable("PROGRAMFILES(X86)"), Environment.GetEnvironmentVariable("PROGRAMFILES") }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Path.Combine(x!, "Steam")).ToList();
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                var parts = line.Split('"');
                if (parts.Length >= 4 && parts[1].Equals("path", StringComparison.OrdinalIgnoreCase)) libraries.Add(parts[3].Replace(@"\\", @"\"));
            }
        }
        var inserted = 0;
        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var name = Field(text, "name"); var folder = Field(text, "installdir");
                if (name is null || folder is null) continue;
                var install = Path.Combine(steamApps, "common", folder);
                if (!Directory.Exists(install) || ExistsInstall(install)) continue;
                var executable = Directory.EnumerateFiles(install, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(x => !Path.GetFileName(x).Contains("uninstall", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(x).Contains("launcher", StringComparison.OrdinalIgnoreCase));
                using var command = _db.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO games(name,executable,install_path,source) VALUES($name,$exe,$path,'steam'); SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$exe", (object?)executable ?? DBNull.Value); command.Parameters.AddWithValue("$path", install);
                var id = (long)command.ExecuteScalar()!;
                if (id > 0) { SaveProfile(new(id, "Balanced", null)); inserted++; }
            }
        }
        return inserted;
    }

    private bool ExistsInstall(string path) { using var c = _db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM games WHERE install_path=$path"; c.Parameters.AddWithValue("$path", path); return (long)c.ExecuteScalar()! > 0; }
    private static string? Field(string text, string key) => text.Split('\n').Select(x => x.Split('"')).Where(x => x.Length >= 4 && x[1].Equals(key, StringComparison.OrdinalIgnoreCase)).Select(x => x[3]).FirstOrDefault();

    public Profile GetProfile(long gameId)
    {
        using var c = _db.CreateCommand(); c.CommandText = "SELECT game_id,preset,power_scheme FROM profiles WHERE game_id=$id"; c.Parameters.AddWithValue("$id", gameId);
        using var r = c.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Profile not found.");
        return new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public void SaveProfile(Profile profile)
    {
        if (profile.Preset is not ("Balanced" or "Performance" or "Custom")) throw new InvalidOperationException("Invalid profile preset.");
        if (profile.Preset == "Balanced" && profile.PowerScheme is not null) throw new InvalidOperationException("Balanced does not change the power plan.");
        if (profile.PowerScheme is not null && !Guid.TryParse(profile.PowerScheme, out _)) throw new InvalidOperationException("Invalid power scheme GUID.");
        using var c = _db.CreateCommand(); c.CommandText = "INSERT INTO profiles(game_id,preset,power_scheme) VALUES($id,$preset,$scheme) ON CONFLICT(game_id) DO UPDATE SET preset=excluded.preset,power_scheme=excluded.power_scheme";
        c.Parameters.AddWithValue("$id", profile.GameId); c.Parameters.AddWithValue("$preset", profile.Preset); c.Parameters.AddWithValue("$scheme", (object?)profile.PowerScheme ?? DBNull.Value); c.ExecuteNonQuery();
    }

    public IReadOnlyList<Session> Sessions()
    {
        using var c = _db.CreateCommand(); c.CommandText = "SELECT s.id,s.game_id,g.name,s.profile_preset,s.started_at,s.ended_at,s.status,s.restoration_result FROM sessions s JOIN games g ON g.id=s.game_id ORDER BY s.id DESC LIMIT 100";
        using var r = c.ExecuteReader(); var result = new List<Session>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4)), r.IsDBNull(5) ? null : DateTimeOffset.Parse(r.GetString(5)), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return result;
    }

    public Recovery? Recovery()
    {
        using var c = _db.CreateCommand(); c.CommandText = "SELECT s.id,g.name,s.previous_scheme,s.applied_scheme,s.status FROM sessions s JOIN games g ON g.id=s.game_id WHERE s.status IN ('prepared','running','restore_failed') ORDER BY s.id DESC LIMIT 1";
        using var r = c.ExecuteReader(); return r.Read() ? new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4)) : null;
    }

    public string ActivePowerScheme() => Power("/getactivescheme").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => Guid.TryParse(x, out _)) ?? throw new InvalidOperationException("Windows did not return a power scheme GUID.");
    private static string Power(string args) { var p = Process.Start(new ProcessStartInfo("powercfg.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, UseShellExecute = false })!; var output = p.StandardOutput.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd()); return output; }
    private static void SetPower(string guid) { _ = Guid.Parse(guid); Power($"/setactive {guid}"); }

    public void Launch(long gameId)
    {
        if (IsRunning) throw new InvalidOperationException("A game session is already running.");
        if (Recovery() is not null) throw new InvalidOperationException("Recover the unfinished session before starting another game.");
        var game = Games().First(x => x.Id == gameId); if (game.Executable is null || !File.Exists(game.Executable)) throw new InvalidOperationException("Add an executable path for this game first.");
        var profile = GetProfile(gameId); var previous = profile.PowerScheme is null ? null : ActivePowerScheme(); var applied = profile.PowerScheme is not null && !profile.PowerScheme.Equals(previous, StringComparison.OrdinalIgnoreCase) ? profile.PowerScheme : null;
        using var c = _db.CreateCommand(); c.CommandText = "INSERT INTO sessions(game_id,started_at,status,previous_scheme,applied_scheme,profile_preset) VALUES($game,$started,'prepared',$previous,$applied,$preset); SELECT last_insert_rowid();";
        c.Parameters.AddWithValue("$game", gameId); c.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O")); c.Parameters.AddWithValue("$previous", (object?)previous ?? DBNull.Value); c.Parameters.AddWithValue("$applied", (object?)applied ?? DBNull.Value); c.Parameters.AddWithValue("$preset", profile.Preset);
        var id = (long)c.ExecuteScalar()!;
        try { if (applied is not null) SetPower(applied); _running = Process.Start(new ProcessStartInfo { FileName = game.Executable, WorkingDirectory = Path.GetDirectoryName(game.Executable), UseShellExecute = false, CreateNoWindow = true }); if (_running is null) throw new InvalidOperationException("Could not start the game."); _running.EnableRaisingEvents = true; _running.Exited += (_, _) => Finish(id, previous, applied, "completed"); UpdateStatus(id, "running", null, false); } catch { Finish(id, previous, applied, "launch_failed"); throw; }
    }

    private void Finish(long id, string? previous, string? applied, string status) { try { if (applied is not null && ActivePowerScheme().Equals(applied, StringComparison.OrdinalIgnoreCase) && previous is not null) SetPower(previous); UpdateStatus(id, status, "Original power plan restored and verified", true); } catch (Exception e) { UpdateStatus(id, "restore_failed", e.Message, false); } finally { _running?.Dispose(); _running = null; } }
    private void UpdateStatus(long id, string status, string? detail, bool finished) { using var c = _db.CreateCommand(); c.CommandText = "UPDATE sessions SET status=$status,restoration_result=$detail,ended_at=$ended WHERE id=$id"; c.Parameters.AddWithValue("$status", status); c.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value); c.Parameters.AddWithValue("$ended", finished ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value); c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery(); }
    public void Recover() { var pending = Recovery() ?? throw new InvalidOperationException("No pending session."); if (pending.PreviousScheme is not null && pending.AppliedScheme is not null && ActivePowerScheme().Equals(pending.AppliedScheme, StringComparison.OrdinalIgnoreCase)) SetPower(pending.PreviousScheme); UpdateStatus(pending.SessionId, "recovered", "Original power plan restored and verified", true); }

    public MonitorSample Sample()
    {
        using var process = Process.GetCurrentProcess(); var now = DateTimeOffset.UtcNow; var cpu = _lastCpuAt == default ? 0 : (float)Math.Clamp((process.TotalProcessorTime - _lastCpu).TotalMilliseconds / Math.Max(1, (now - _lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100, 0, 100); _lastCpu = process.TotalProcessorTime; _lastCpuAt = now;
        var info = new MEMORYSTATUSEX(); info.dwLength = (uint)Marshal.SizeOf(info); GlobalMemoryStatusEx(ref info); return new(cpu, info.ullTotalPhys - info.ullAvailPhys, info.ullTotalPhys);
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX { public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    public void Dispose() { _running?.Dispose(); _db.Dispose(); }
}
