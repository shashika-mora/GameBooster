namespace GameBooster;

public sealed record Game(long Id, string Name, string? Executable, string? InstallPath, string Source);
public sealed record Profile(long GameId, string Preset, string? PowerScheme);
public sealed record Session(long Id, long GameId, string GameName, string ProfilePreset, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Status, string? RestorationResult);
public sealed record Recovery(long SessionId, string GameName, string? PreviousScheme, string? AppliedScheme, string Status);
public sealed record MonitorSample(float CpuPercent, ulong UsedMemoryBytes, ulong TotalMemoryBytes);
