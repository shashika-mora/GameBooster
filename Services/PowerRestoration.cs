namespace GameBooster.Services;

public static class ProfileRules
{
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static Profile Normalize(Profile profile)
    {
        if (profile.Preset is not ("Balanced" or "Performance" or "Custom"))
            throw new InvalidOperationException("Invalid profile preset.");
        if (profile.PriorityPreference is not ("Unchanged" or "AboveNormal"))
            throw new InvalidOperationException("Choose the default or above-normal game priority.");
        Guid? scheme = null;
        if (!string.IsNullOrWhiteSpace(profile.PowerScheme))
        {
            if (!Guid.TryParseExact(profile.PowerScheme.Trim(), "D", out var parsed))
                throw new InvalidOperationException("Choose a valid power plan.");
            scheme = parsed;
        }
        if (profile.Preset == "Balanced" && scheme is not null)
            throw new InvalidOperationException("Balanced does not change the power plan.");
        if (profile.Preset == "Balanced" && (profile.PriorityPreference != "Unchanged" || profile.KeepAwake))
            throw new InvalidOperationException("Choose Custom to add session settings to the Balanced preset.");
        if (profile.Preset == "Performance")
        {
            if (scheme is not null && scheme != HighPerformance)
                throw new InvalidOperationException("Performance uses the Windows High performance plan.");
            scheme = HighPerformance;
        }
        return profile with { PowerScheme = scheme?.ToString("D") };
    }
}

public static class PowerRestoration
{
    public static void ApplyAndVerify(IPowerPlans power, Guid target)
    {
        power.SetScheme(target);
        if (power.ActiveScheme() != target)
            throw new InvalidOperationException("The power-plan change could not be verified.");
    }

    public static string Restore(IPowerPlans power, string? previous, string? applied)
    {
        if (applied is null) return "No system setting changed.";
        if (!Guid.TryParse(previous, out var oldScheme) || !Guid.TryParse(applied, out var newScheme))
            throw new InvalidOperationException("The saved recovery snapshot is invalid. The power plan needs manual review.");
        var current = power.ActiveScheme();
        if (current == oldScheme) return "Original power plan already restored and verified.";
        if (current != newScheme)
            throw new InvalidOperationException("The power plan changed outside GameBooster. Select the original plan in Windows, then retry recovery.");
        ApplyAndVerify(power, oldScheme);
        return "Original power plan restored and verified.";
    }
}
