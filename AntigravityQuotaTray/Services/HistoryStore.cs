using System.IO;
using System.Text;
using System.Text.Json;
using AntigravityQuotaTray.Models;

namespace AntigravityQuotaTray.Services;

public sealed class HistoryStore
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string DirectoryPath { get; }
    public string FilePath { get; }

    public HistoryStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntigravityQuotaTray");
        FilePath = Path.Combine(DirectoryPath, "history.jsonl");
    }

    public void Append(QuotaSnapshot snapshot)
    {
        if (snapshot.Gemini5h is null && snapshot.GeminiWeekly is null)
        {
            return;
        }

        var point = new HistoryPoint(
            snapshot.TimestampUtc,
            snapshot.Gemini5h?.RemainingPercent,
            snapshot.GeminiWeekly?.RemainingPercent,
            snapshot.Claude5h?.RemainingPercent,
            snapshot.ClaudeWeekly?.RemainingPercent,
            snapshot.RemainingCredits);

        var line = JsonSerializer.Serialize(point, JsonOptions);

        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllLines(FilePath, [line], Encoding.UTF8);
        }
    }

    public IReadOnlyList<HistoryPoint> LoadRange(TimeSpan window, DateTimeOffset? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow) - window;
        var points = new List<HistoryPoint>();

        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return points;
            }

            foreach (var rawLine in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                try
                {
                    var point = JsonSerializer.Deserialize<HistoryPoint>(rawLine, JsonOptions);
                    if (point is not null && point.T >= cutoff)
                    {
                        points.Add(point);
                    }
                }
                catch
                {
                    // Skip malformed line
                }
            }
        }

        points.Sort((a, b) => a.T.CompareTo(b.T));
        return points;
    }

    public void PruneOlderThan(TimeSpan retention, DateTimeOffset? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTimeOffset.UtcNow) - retention;

        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var keptLines = new List<string>();
            var removedAny = false;

            foreach (var rawLine in File.ReadLines(FilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                try
                {
                    var point = JsonSerializer.Deserialize<HistoryPoint>(rawLine, JsonOptions);
                    if (point is not null && point.T >= cutoff)
                    {
                        keptLines.Add(rawLine);
                    }
                    else
                    {
                        removedAny = true;
                    }
                }
                catch
                {
                    removedAny = true;
                }
            }

            if (!removedAny)
            {
                return;
            }

            var tmpPath = FilePath + ".tmp";
            File.WriteAllLines(tmpPath, keptLines, Encoding.UTF8);
            File.Move(tmpPath, FilePath, overwrite: true);
        }
    }
}
