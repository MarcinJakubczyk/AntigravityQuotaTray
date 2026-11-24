using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AntigravityQuotaTray.Models;

namespace AntigravityQuotaTray.Services;

public sealed class AgyQuotaFetcher : IDisposable
{
    private readonly int _timeoutSeconds;
    private readonly AppLogger? _logger;
    private readonly HiddenProcessRunner _processRunner = new();

    public AgyQuotaFetcher(int timeoutSeconds = 15, AppLogger? logger = null)
    {
        _timeoutSeconds = Math.Max(5, timeoutSeconds);
        _logger = logger;
    }

    public void Dispose()
    {
        _processRunner.Dispose();
    }

    public static string? ResolveAgyPath()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "agy.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid PATH entries
            }
        }

        var profileExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "bin", "agy.exe");
        if (File.Exists(profileExe))
        {
            return profileExe;
        }

        return null;
    }

    public static bool? ReadUseAiCreditsSetting()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "config", "config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }

            using var stream = File.OpenRead(configPath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("userSettings", out var userSettings) &&
                userSettings.TryGetProperty("useAiCredits", out var flag) &&
                (flag.ValueKind == JsonValueKind.True || flag.ValueKind == JsonValueKind.False))
            {
                return flag.GetBoolean();
            }
        }
        catch
        {
            // Best-effort read
        }

        return null;
    }

    public Task<QuotaSnapshot> FetchAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(AppLanguage.En, cancellationToken);

    public async Task<QuotaSnapshot> FetchAsync(AppLanguage language, CancellationToken cancellationToken = default)
    {
        var strings = AppStrings.For(language);
        var nowUtc = DateTimeOffset.UtcNow;
        var useAiCredits = ReadUseAiCreditsSetting();
        var agyPath = ResolveAgyPath();
        if (string.IsNullOrEmpty(agyPath))
        {
            _logger?.Warn("agy.exe not found in PATH or ~/.gemini/bin.");
            return QuotaSnapshot.Failure(nowUtc, strings.ErrAgyNotFound, useAiCredits);
        }

        try
        {
            var usageTask = RunAgyCommandAsync(agyPath, "/usage", cancellationToken);
            var creditsTask = RunAgyCommandAsync(agyPath, "/credits", cancellationToken);

            await Task.WhenAll(usageTask, creditsTask).ConfigureAwait(false);

            var usageRes = await usageTask.ConfigureAwait(false);
            var creditsRes = await creditsTask.ConfigureAwait(false);

            if (usageRes.ExitCode != 0 || string.IsNullOrWhiteSpace(usageRes.Stdout))
            {
                var err = !string.IsNullOrWhiteSpace(usageRes.Stderr)
                    ? usageRes.Stderr.Trim()
                    : string.Format(CultureInfo.InvariantCulture, strings.ErrUsageExitCodeFormat, usageRes.ExitCode);
                _logger?.Warn($"agy /usage failed (exit={usageRes.ExitCode}): {err}");
                return QuotaSnapshot.Failure(nowUtc, err, useAiCredits);
            }

            if (creditsRes.ExitCode != 0)
            {
                _logger?.Warn($"agy /credits returned exit={creditsRes.ExitCode}: {creditsRes.Stderr.Trim()}");
            }

            var creditsStdout = creditsRes.ExitCode == 0 ? creditsRes.Stdout : null;
            return ParseResponses(usageRes.Stdout, creditsStdout, useAiCredits, nowUtc, language);
        }
        catch (OperationCanceledException ex)
        {
            _logger?.Warn($"agy.exe timed out after {_timeoutSeconds}s.", ex);
            return QuotaSnapshot.Failure(nowUtc, strings.ErrAgyTimeout, useAiCredits);
        }
        catch (Exception ex)
        {
            _logger?.Error("agy.exe execution failed.", ex);
            return QuotaSnapshot.Failure(
                nowUtc,
                string.Format(CultureInfo.InvariantCulture, strings.ErrAgyExecutionFormat, ex.Message),
                useAiCredits);
        }
    }

    public static QuotaSnapshot ParseResponses(
        string usageJson,
        string? creditsJson,
        bool? useAiCreditsEnabled,
        DateTimeOffset nowUtc,
        AppLanguage language = AppLanguage.En)
    {
        var strings = AppStrings.For(language);
        try
        {
            using var usageDoc = JsonDocument.Parse(usageJson);
            var root = usageDoc.RootElement;

            if (!root.TryGetProperty("command", out var cmd) ||
                !cmd.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("groups", out var groups) ||
                groups.ValueKind != JsonValueKind.Array)
            {
                return QuotaSnapshot.Failure(nowUtc, strings.ErrUnexpectedUsageJson, useAiCreditsEnabled);
            }

            QuotaBucket? gemini5h = null;
            QuotaBucket? geminiWeekly = null;
            QuotaBucket? claude5h = null;
            QuotaBucket? claudeWeekly = null;

            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("name", out var gn) ? gn.GetString() ?? string.Empty : string.Empty;
                if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var isGemini = groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
                var isClaudeOrGpt = groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                                    groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase);

                foreach (var bucket in buckets.EnumerateArray())
                {
                    var limitType = bucket.TryGetProperty("name", out var bn) ? bn.GetString() ?? string.Empty : string.Empty;
                    var window = bucket.TryGetProperty("window", out var bw) ? bw.GetString() ?? string.Empty : string.Empty;

                    double fraction = 0;
                    if (bucket.TryGetProperty("remaining_fraction", out var rf) && rf.TryGetDouble(out var parsedFraction))
                    {
                        fraction = Math.Clamp(parsedFraction, 0.0, 1.0);
                    }

                    var percent = Math.Clamp((int)Math.Round(fraction * 100.0, MidpointRounding.AwayFromZero), 0, 100);
                    var resetUtc = DateTimeOffset.MinValue;
                    if (bucket.TryGetProperty("reset_time", out var rt))
                    {
                        var resetStr = rt.GetString();
                        if (!string.IsNullOrWhiteSpace(resetStr))
                        {
                            DateTimeOffset.TryParse(
                                resetStr,
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                out resetUtc);
                        }
                    }

                    var parsedBucket = new QuotaBucket(
                        groupName,
                        limitType,
                        window,
                        fraction,
                        percent,
                        QuotaSnapshot.StatusFromPercent(percent),
                        resetUtc);

                    if (isGemini)
                    {
                        if (window.Equals("5h", StringComparison.OrdinalIgnoreCase))
                        {
                            gemini5h = parsedBucket;
                        }
                        else if (window.Equals("weekly", StringComparison.OrdinalIgnoreCase))
                        {
                            geminiWeekly = parsedBucket;
                        }
                    }
                    else if (isClaudeOrGpt)
                    {
                        if (window.Equals("5h", StringComparison.OrdinalIgnoreCase))
                        {
                            claude5h = parsedBucket;
                        }
                        else if (window.Equals("weekly", StringComparison.OrdinalIgnoreCase))
                        {
                            claudeWeekly = parsedBucket;
                        }
                    }
                }
            }

            int? remainingCredits = ParseCreditsJson(creditsJson);

            return new QuotaSnapshot(
                nowUtc,
                gemini5h,
                geminiWeekly,
                claude5h,
                claudeWeekly,
                remainingCredits,
                useAiCreditsEnabled,
                null);
        }
        catch (JsonException ex)
        {
            return QuotaSnapshot.Failure(
                nowUtc,
                string.Format(CultureInfo.InvariantCulture, strings.ErrJsonParseFormat, ex.Message),
                useAiCreditsEnabled);
        }
        catch (Exception ex)
        {
            return QuotaSnapshot.Failure(
                nowUtc,
                string.Format(CultureInfo.InvariantCulture, strings.ErrDataAnalysisFormat, ex.Message),
                useAiCreditsEnabled);
        }
    }

    private static int? ParseCreditsJson(string? creditsJson)
    {
        if (string.IsNullOrWhiteSpace(creditsJson))
        {
            return null;
        }

        try
        {
            using var creditsDoc = JsonDocument.Parse(creditsJson);
            if (creditsDoc.RootElement.TryGetProperty("command", out var cmd) &&
                cmd.TryGetProperty("data", out var data) &&
                data.TryGetProperty("remaining_credits", out var rc) &&
                rc.TryGetInt32(out var credits))
            {
                return credits;
            }
        }
        catch
        {
            // Non-fatal
        }

        return null;
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAgyCommandAsync(
        string agyPath,
        string slashCommand,
        CancellationToken cancellationToken)
    {
        var arguments = $"-p \"{slashCommand}\" --output-format json --print-timeout {_timeoutSeconds}s";
        return _processRunner.RunAsync(
            agyPath,
            arguments,
            Path.GetTempPath(),
            TimeSpan.FromSeconds(_timeoutSeconds + 5),
            cancellationToken);
    }
}
