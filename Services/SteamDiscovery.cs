using Microsoft.Win32;

namespace GameBooster.Services;

public sealed record DiscoveredGame(string Name, string InstallPath, string? Executable);

public static class SteamDiscovery
{
    public static IEnumerable<string> DefaultRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path) roots.Add(path);
        }
        foreach (var variable in new[] { "PROGRAMFILES(X86)", "PROGRAMFILES" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } folder) roots.Add(Path.Combine(folder, "Steam"));
        return roots;
    }

    public static IReadOnlyList<DiscoveredGame> Scan(IEnumerable<string> roots)
    {
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in libraries.ToArray())
        {
            var manifest = ReadText(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
            if (manifest is null) continue;
            foreach (var line in manifest.Split('\n'))
                if (Field(line, "path") is { } path) libraries.Add(path.Replace(@"\\", @"\"));
        }
        var result = new List<DiscoveredGame>();
        foreach (var library in libraries)
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                foreach (var manifestPath in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    var text = ReadText(manifestPath);
                    if (text is null || Field(text, "name") is not { } name || Field(text, "installdir") is not { } folder) continue;
                    var common = Path.GetFullPath(Path.Combine(apps, "common")) + Path.DirectorySeparatorChar;
                    var install = Path.GetFullPath(Path.Combine(common, folder));
                    if (!install.StartsWith(common, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(install)) continue;
                    result.Add(new(name, install, FindExecutable(install, name)));
                }
            }
            catch (IOException) { /* A library can disappear during a scan. */ }
            catch (UnauthorizedAccessException) { /* Other accessible libraries remain usable. */ }
        }
        return result;
    }

    public static string? Field(string text, string key) => text.Split('\n').Select(line => line.Split('"'))
        .Where(parts => parts.Length >= 4 && parts[1].Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(parts => parts[3]).FirstOrDefault();

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? FindExecutable(string directory, string name)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(directory, "*.exe").Where(path =>
                !new[] { "uninstall", "setup", "launcher", "redist" }.Any(word => Path.GetFileName(path).Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
            var matching = candidates.Where(path => Path.GetFileNameWithoutExtension(path).Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)).ToArray();
            return matching.Length == 1 ? matching[0] : candidates.Length == 1 ? candidates[0] : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
