namespace AntigravityQuotaTray.Models;

public sealed record ModelUsageEvent(
    DateTimeOffset TimestampUtc,
    string RawModel,
    string FamilyKey,
    string DisplayName,
    string ShortLabel,
    double WeightedTokens,
    long InputTokens,
    long CacheReadTokens,
    long OutputTokens);

public sealed record ModelShare(
    string FamilyKey,
    string DisplayName,
    string ShortLabel,
    double Share,
    double WeightedTokens,
    int CallCount);

public sealed record ActivityBucket(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    IReadOnlyList<ModelShare> Models,
    double TotalWeightedTokens);

public sealed record DropAnnotation(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int StartPercent,
    int EndPercent,
    int DropPercent,
    IReadOnlyList<ModelShare> Models);

public static class ModelNameFormatter
{
    public static (string FamilyKey, string DisplayName, string ShortLabel) Parse(string? rawModel)
    {
        if (string.IsNullOrWhiteSpace(rawModel))
        {
            return ("unknown", "Unknown", "?");
        }

        var normalized = rawModel.Trim().ToLowerInvariant();
        var (effortDisplay, effortShort) = ParseEffortSuffix(normalized);

        if (normalized.Contains("orchestrator", StringComparison.Ordinal))
        {
            if (normalized.Contains("neon", StringComparison.Ordinal))
            {
                return ("orchestrator", "Orch (Neon)", "Orch-N");
            }

            if (normalized.Contains("carbon", StringComparison.Ordinal))
            {
                return ("orchestrator", "Orch (Carbon)", "Orch-C");
            }

            if (normalized.Contains("argon", StringComparison.Ordinal))
            {
                return ("orchestrator", "Orch (Argon)", "Orch-A");
            }

            return ("orchestrator", "Orchestrator", "Orch");
        }

        if (normalized.Contains("hydrogen", StringComparison.Ordinal))
        {
            return FormatWithEffort("hydrogen", "Hydrogen", "H", effortDisplay, effortShort);
        }

        if (normalized.Contains("carbon", StringComparison.Ordinal))
        {
            return FormatWithEffort("carbon", "Carbon", "C", effortDisplay, effortShort);
        }

        if (normalized.Contains("neon", StringComparison.Ordinal))
        {
            return FormatWithEffort("neon", "Neon", "N", effortDisplay, effortShort);
        }

        if (normalized.Contains("argon", StringComparison.Ordinal))
        {
            return FormatWithEffort("argon", "Argon", "A", effortDisplay, effortShort);
        }

        if (normalized.Contains("flash", StringComparison.Ordinal))
        {
            return ("flash", "Gemini Flash", "Flash");
        }

        if (normalized.StartsWith("claude-opus", StringComparison.Ordinal) ||
            normalized.Contains("opus", StringComparison.Ordinal))
        {
            return effortDisplay is null
                ? ("claude-opus", "Claude Opus", "Opus")
                : ("claude-opus", $"Opus {effortDisplay}", $"Opus-{effortShort}");
        }

        if (normalized.StartsWith("claude-sonnet", StringComparison.Ordinal) ||
            normalized.Contains("sonnet", StringComparison.Ordinal))
        {
            return effortDisplay is null
                ? ("claude-sonnet", "Claude Sonnet", "Sonnet")
                : ("claude-sonnet", $"Sonnet {effortDisplay}", $"Son-{effortShort}");
        }

        if (normalized.StartsWith("gpt", StringComparison.Ordinal))
        {
            return ("gpt", "GPT", "GPT");
        }

        var trimmed = rawModel.Trim();
        var fallbackShort = trimmed.Length <= 6 ? trimmed : trimmed[..6];
        return (normalized, trimmed, fallbackShort);
    }

    public static string GetFamilyDisplayName(string familyKey) => familyKey switch
    {
        "hydrogen" => "Hydrogen",
        "carbon" => "Carbon",
        "neon" => "Neon",
        "argon" => "Argon",
        "orchestrator" => "Orchestrator",
        "flash" => "Flash",
        "claude-opus" => "Opus",
        "claude-sonnet" => "Sonnet",
        "gpt" => "GPT",
        _ => string.IsNullOrWhiteSpace(familyKey) ? "Other" : familyKey
    };

    public static (byte R, byte G, byte B) GetFamilyRgb(string familyKey) => familyKey switch
    {
        "hydrogen" => (250, 204, 21),      // #FACC15 vivid yellow
        "carbon" => (255, 99, 146),        // #FF6392 warm rose-pink
        "neon" => (168, 85, 247),          // #A855F7 electric neon violet
        "argon" => (248, 113, 113),        // #F87171 coral red
        "orchestrator" => (248, 250, 252), // #F8FAFC crisp ice-silver
        "flash" => (163, 230, 53),         // #A3E635 bright lime
        "claude-opus" => (251, 146, 60),   // #FB923C vivid orange
        "claude-sonnet" => (245, 158, 11), // #F59E0B amber
        "gpt" => (56, 189, 248),           // #38BDF8 sky blue
        _ => (148, 163, 184)               // #94A3B8 neutral slate
    };

    private static (string? Display, string? Short) ParseEffortSuffix(string normalized)
    {
        if (normalized.Contains("xhigh", StringComparison.Ordinal))
        {
            return ("xHigh", "xH");
        }

        if (normalized.Contains("high", StringComparison.Ordinal))
        {
            return ("High", "Hi");
        }

        if (normalized.Contains("medium", StringComparison.Ordinal))
        {
            return ("Med", "Med");
        }

        if (normalized.Contains("low", StringComparison.Ordinal))
        {
            return ("Low", "Lo");
        }

        return (null, null);
    }

    private static (string FamilyKey, string DisplayName, string ShortLabel) FormatWithEffort(
        string familyKey,
        string familyDisplay,
        string shortPrefix,
        string? effortDisplay,
        string? effortShort) =>
        effortDisplay is null
            ? (familyKey, familyDisplay, shortPrefix)
            : (familyKey, $"{familyDisplay} {effortDisplay}", $"{shortPrefix}-{effortShort}");
}
