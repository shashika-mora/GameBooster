namespace GameBooster;

public sealed record Game(long Id, string Name, string? Executable, string? InstallPath, string Source)
{
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    public string SourceLabel => Source == "steam" ? "Steam library" : "Manually added";
}
public sealed record Profile(long GameId, string Preset, string? PowerScheme, string PriorityPreference = "Unchanged", bool KeepAwake = false);
public sealed record AppSettings(string Theme = "System", int SamplingSeconds = 3);
public sealed record PowerPlanOption(string? Scheme, string Name);
public sealed record OperationRecord(long Id, string Operation, string ApplyStatus, string Status, string? Detail)
{
    public string Name => Operation switch { "power_scheme" => "Power plan", "process_priority" => "Game CPU priority", "keep_awake" => "Prevent idle sleep", _ => "Session setting" };
    public string Summary => $"{ApplyStatus.Replace('_', ' ')} · {Status.Replace('_', ' ')}";
}
public sealed record Session(long Id, long GameId, string GameName, string ProfilePreset, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Status, string? RestorationResult)
{
    public string StartedLabel => StartedAt.LocalDateTime.ToString("g");
    public string StatusLabel => Status switch { "completed" => "Completed", "running" => "Playing", "prepared" => "Preparing", "restore_failed" => "Needs recovery", "recovered" => "Recovered", "interrupted" => "Stopped · restored", "launch_failed" => "Launch failed", "apply_failed" => "Setting unavailable", _ => "Recorded" };
    public string DurationLabel
    {
        get
        {
            if (EndedAt is null && Status is not ("running" or "prepared")) return "Unfinished";
            var elapsed = (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;
            return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m" : $"{Math.Max(0, (int)elapsed.TotalMinutes)}m {Math.Max(0, elapsed.Seconds)}s";
        }
    }
}
public sealed record Recovery(long SessionId, string GameName, string? PreviousScheme, string? AppliedScheme, string Status);
public sealed record MonitorSample(float CpuPercent, ulong UsedMemoryBytes, ulong TotalMemoryBytes);
