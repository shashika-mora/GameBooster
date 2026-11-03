namespace GameBooster.Services;

public sealed class AppLog(string directory)
{
    private readonly object _gate = new();

    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "gamebooster.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* Logging must not prevent restoration. */ }
        catch (UnauthorizedAccessException) { /* The durable snapshot remains in SQLite. */ }
    }
}
