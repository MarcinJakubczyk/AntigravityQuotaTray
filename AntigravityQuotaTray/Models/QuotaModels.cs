using System.Globalization;

namespace AntigravityQuotaTray.Models;

public enum QuotaStatus
{
    Unknown,
    Ok,
    Moderate,
    Low
}

public enum TrayIconStyle
{
    NumberWithWeeklyBar,
    DualBars,
    RingGauge
}

public enum AppLanguage
{
    En,
    Pl
}

public sealed record QuotaBucket(
    string ModelGroup,
    string LimitType,
    string Window,
    double RemainingFraction,
    int RemainingPercent,
    QuotaStatus Status,
    DateTimeOffset ResetUtc)
{
    public string ResetLocalText
    {
        get
        {
            if (ResetUtc == DateTimeOffset.MinValue)
            {
                return "-";
            }

            var local = ResetUtc.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            return local.Date == today
                ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        }
    }

    public string FormatResetIn(DateTimeOffset nowUtc, AppLanguage language = AppLanguage.En)
    {
        if (ResetUtc == DateTimeOffset.MinValue)
        {
            return "-";
        }

        var delta = ResetUtc.ToUniversalTime() - nowUtc.ToUniversalTime();
        if (delta.TotalSeconds <= 0)
        {
            return language == AppLanguage.Pl ? "teraz" : "now";
        }

        if (delta.Days > 0)
        {
            return $"{delta.Days}d {delta.Hours}h";
        }

        if (delta.Hours > 0)
        {
            return $"{delta.Hours}h {delta.Minutes}m";
        }

        return $"{Math.Max(1, delta.Minutes)}m";
    }

    public TimeSpan? GetWindowDuration()
    {
        if (Window.Equals("5h", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(5);
        }

        if (Window.Equals("weekly", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromDays(7);
        }

        return null;
    }

    /// <summary>
    /// Returns rising progress toward reset in range [0.0, 1.0]:
    /// 0.0 at window start, 1.0 when reset is due. Returns null if reset or window is unknown.
    /// </summary>
    public double? GetResetProgressFraction(DateTimeOffset nowUtc)
    {
        if (ResetUtc == DateTimeOffset.MinValue)
        {
            return null;
        }

        var duration = GetWindowDuration();
        if (duration is null || duration.Value.TotalSeconds <= 0)
        {
            return null;
        }

        var remainingSeconds = (ResetUtc.ToUniversalTime() - nowUtc.ToUniversalTime()).TotalSeconds;
        var elapsedFraction = 1.0 - (remainingSeconds / duration.Value.TotalSeconds);
        return Math.Clamp(elapsedFraction, 0.0, 1.0);
    }
}

public sealed record QuotaSnapshot(
    DateTimeOffset TimestampUtc,
    QuotaBucket? Gemini5h,
    QuotaBucket? GeminiWeekly,
    QuotaBucket? Claude5h,
    QuotaBucket? ClaudeWeekly,
    int? RemainingCredits,
    bool? UseAiCreditsEnabled,
    string? ErrorMessage)
{
    public static QuotaStatus StatusFromPercent(int percent) => percent switch
    {
        < 20 => QuotaStatus.Low,
        < 40 => QuotaStatus.Moderate,
        _ => QuotaStatus.Ok
    };

    public static QuotaSnapshot Failure(DateTimeOffset timestampUtc, string errorMessage, bool? useAiCreditsEnabled = null) =>
        new(timestampUtc, null, null, null, null, null, useAiCreditsEnabled, errorMessage);
}

public sealed record HistoryPoint(
    DateTimeOffset T,
    int? G5,
    int? Gw,
    int? C5,
    int? Cw,
    int? Cr);

public sealed class AppSettings
{
    public int PollIntervalMinutes { get; set; } = 1;
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.NumberWithWeeklyBar;
    public bool OpenOnHover { get; set; } = true;
    public bool AutostartEnabled { get; set; } = true;
    public int ChartRangeHours { get; set; } = 24;
    public bool ShowClaudeOnChart { get; set; } = false;
    public AppLanguage Language { get; set; } = AppLanguage.En;
}
