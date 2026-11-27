using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AntigravityQuotaTray.Models;

namespace AntigravityQuotaTray.Services;

public sealed class ModelActivityTracker
{
    private const double MinConcurrentShare = 0.15;
    private const double DominantSingleShare = 0.80;

    private sealed class CachedTranscript
    {
        public DateTime LastWriteUtc { get; set; }
        public long LastByteOffset { get; set; }
        public List<ModelUsageEvent> Events { get; } = [];
    }

    private readonly string _brainRootDirectory;
    private readonly AppLogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedTranscript> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModelActivityTracker(string? brainRootDirectory = null, AppLogger? logger = null)
    {
        _brainRootDirectory = brainRootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity", "brain");
        _logger = logger;
    }

    public IReadOnlyList<ModelUsageEvent> LoadEvents(TimeSpan window, DateTimeOffset? nowUtc = null)
    {
        var cutoffUtc = (nowUtc ?? DateTimeOffset.UtcNow) - window;
        var results = new List<ModelUsageEvent>();

        lock (_gate)
        {
            if (!Directory.Exists(_brainRootDirectory))
            {
                return results;
            }

            IEnumerable<string> brainDirs;
            try
            {
                brainDirs = Directory.EnumerateDirectories(_brainRootDirectory);
            }
            catch (Exception ex)
            {
                _logger?.Warn("Failed to enumerate brain directories.", ex);
                return results;
            }

            foreach (var dir in brainDirs)
            {
                var transcriptPath = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                FileInfo fileInfo;
                try
                {
                    if (!File.Exists(transcriptPath))
                    {
                        continue;
                    }

                    fileInfo = new FileInfo(transcriptPath);
                }
                catch
                {
                    continue;
                }

                var hasCache = _cache.TryGetValue(transcriptPath, out var cached);
                var fileWriteUtc = fileInfo.LastWriteTimeUtc;

                if (!hasCache && fileWriteUtc < cutoffUtc.UtcDateTime)
                {
                    continue;
                }

                if (cached is null)
                {
                    cached = new CachedTranscript();
                    _cache[transcriptPath] = cached;
                }

                if (!hasCache || cached.LastWriteUtc != fileWriteUtc || fileInfo.Length != cached.LastByteOffset)
                {
                    ReadIncremental(transcriptPath, cached, fileInfo.Length, fileWriteUtc);
                }

                foreach (var ev in cached.Events)
                {
                    if (ev.TimestampUtc >= cutoffUtc)
                    {
                        results.Add(ev);
                    }
                }
            }
        }

        results.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        return results;
    }

    private void ReadIncremental(string transcriptPath, CachedTranscript cached, long knownLength, DateTime fileWriteUtc)
    {
        try
        {
            using var stream = new FileStream(
                transcriptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < cached.LastByteOffset)
            {
                cached.Events.Clear();
                cached.LastByteOffset = 0;
            }

            if (stream.Length == cached.LastByteOffset)
            {
                cached.LastWriteUtc = fileWriteUtc;
                return;
            }

            stream.Seek(cached.LastByteOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

            var validOffset = cached.LastByteOffset;
            while (true)
            {
                var rawLine = ReadLineWithByteCount(stream, reader, ref validOffset, out var endOfStream, out var lineStartOffset);
                if (endOfStream && rawLine is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                if (!rawLine.Contains("PLANNER_RESPONSE", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryParsePlannerResponse(rawLine, out var parsedEvent, out var isJsonMalformed))
                {
                    if (parsedEvent is not null)
                    {
                        cached.Events.Add(parsedEvent);
                    }
                }
                else if (isJsonMalformed && endOfStream)
                {
                    // Antigravity might be mid-write on the last line; keep offset before this line.
                    validOffset = lineStartOffset;
                    break;
                }
            }

            cached.LastByteOffset = validOffset;
            cached.LastWriteUtc = fileWriteUtc;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed reading transcript '{transcriptPath}' (len={knownLength}).", ex);
        }
    }

    /// <summary>
    /// Reads lines directly from UTF-8 bytes on the stream so byte offsets remain exact even with multi-byte chars.
    /// </summary>
    private static string? ReadLineWithByteCount(
        Stream stream,
        StreamReader _,
        ref long validOffset,
        out bool endOfStream,
        out long lineStartOffset)
    {
        lineStartOffset = validOffset;
        stream.Position = validOffset;

        var buffer = new List<byte>(256);
        var b = stream.ReadByte();
        if (b == -1)
        {
            endOfStream = true;
            return null;
        }

        while (b != -1 && b != '\n')
        {
            if (b != '\r')
            {
                buffer.Add((byte)b);
            }

            b = stream.ReadByte();
        }

        endOfStream = b == -1;
        validOffset = stream.Position;

        if (buffer.Count == 0)
        {
            return string.Empty;
        }

        // Strip UTF-8 BOM if present at byte 0.
        var span = buffer.ToArray().AsSpan();
        if (lineStartOffset == 0 && span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        return Encoding.UTF8.GetString(span);
    }

    private static bool TryParsePlannerResponse(string line, out ModelUsageEvent? usageEvent, out bool isJsonMalformed)
    {
        usageEvent = null;
        isJsonMalformed = false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("type", out var typeEl) ||
                !string.Equals(typeEl.GetString(), "PLANNER_RESPONSE", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("model", out var modelEl))
            {
                return false;
            }

            var rawModel = modelEl.GetString();
            if (string.IsNullOrWhiteSpace(rawModel))
            {
                return false;
            }

            if (!root.TryGetProperty("created_at", out var createdAtEl))
            {
                return false;
            }

            var createdAtStr = createdAtEl.GetString();
            if (string.IsNullOrWhiteSpace(createdAtStr) ||
                !DateTimeOffset.TryParse(
                    createdAtStr,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var timestampUtc))
            {
                return false;
            }

            long inputTokens = TryReadInt64(root, "input_tokens");
            long cacheReadTokens = TryReadInt64(root, "cache_read_tokens");
            long outputTokens = TryReadInt64(root, "output_tokens");

            var weighted = inputTokens + (0.1 * cacheReadTokens) + (4.0 * outputTokens);
            if (weighted <= 0)
            {
                weighted = 1.0;
            }

            var (familyKey, displayName, shortLabel) = ModelNameFormatter.Parse(rawModel);
            usageEvent = new ModelUsageEvent(
                timestampUtc,
                rawModel,
                familyKey,
                displayName,
                shortLabel,
                weighted,
                inputTokens,
                cacheReadTokens,
                outputTokens);

            return true;
        }
        catch (JsonException)
        {
            isJsonMalformed = true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static long TryReadInt64(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out var el) && el.TryGetInt64(out var val))
        {
            return Math.Max(0, val);
        }

        return 0;
    }

    public static IReadOnlyList<ModelShare> SummarizeWindow(
        IReadOnlyList<ModelUsageEvent> sortedEvents,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        if (sortedEvents.Count == 0 || endUtc < startUtc)
        {
            return [];
        }

        var startIdx = LowerBound(sortedEvents, startUtc);
        if (startIdx >= sortedEvents.Count)
        {
            return [];
        }

        var groups = new Dictionary<string, (string FamilyKey, string DisplayName, string ShortLabel, double Weighted, int Count)>(StringComparer.Ordinal);
        double totalWeighted = 0;

        for (var i = startIdx; i < sortedEvents.Count; i++)
        {
            var ev = sortedEvents[i];
            if (ev.TimestampUtc > endUtc)
            {
                break;
            }

            totalWeighted += ev.WeightedTokens;
            if (groups.TryGetValue(ev.DisplayName, out var acc))
            {
                groups[ev.DisplayName] = (acc.FamilyKey, acc.DisplayName, acc.ShortLabel, acc.Weighted + ev.WeightedTokens, acc.Count + 1);
            }
            else
            {
                groups[ev.DisplayName] = (ev.FamilyKey, ev.DisplayName, ev.ShortLabel, ev.WeightedTokens, 1);
            }
        }

        if (groups.Count == 0 || totalWeighted <= 0)
        {
            return [];
        }

        var shares = new List<ModelShare>(groups.Count);
        foreach (var g in groups.Values)
        {
            shares.Add(new ModelShare(
                g.FamilyKey,
                g.DisplayName,
                g.ShortLabel,
                g.Weighted / totalWeighted,
                g.Weighted,
                g.Count));
        }

        shares.Sort((a, b) => b.WeightedTokens.CompareTo(a.WeightedTokens));
        return shares;
    }

    public static string? FormatCompactSummary(IReadOnlyList<ModelShare> shares)
    {
        if (shares.Count == 0)
        {
            return null;
        }

        if (shares.Count == 1 || shares[0].Share >= DominantSingleShare)
        {
            return shares[0].DisplayName;
        }

        var significant = new List<string>(3);
        foreach (var share in shares)
        {
            if (share.Share < MinConcurrentShare)
            {
                continue;
            }

            var pct = (int)Math.Round(share.Share * 100.0, MidpointRounding.AwayFromZero);
            significant.Add($"{share.DisplayName} ({pct}%)");
            if (significant.Count == 3)
            {
                break;
            }
        }

        return significant.Count > 0
            ? string.Join(", ", significant)
            : shares[0].DisplayName;
    }

    public static IReadOnlyList<ActivityBucket> BuildActivityBuckets(
        IReadOnlyList<ModelUsageEvent> sortedEvents,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int bucketCount = 96)
    {
        if (sortedEvents.Count == 0 || bucketCount <= 0 || endUtc <= startUtc)
        {
            return [];
        }

        var totalSeconds = (endUtc - startUtc).TotalSeconds;
        var bucketSeconds = totalSeconds / bucketCount;
        var buckets = new List<ActivityBucket>();

        for (var i = 0; i < bucketCount; i++)
        {
            var bStart = startUtc.AddSeconds(i * bucketSeconds);
            var bEnd = i == bucketCount - 1 ? endUtc : startUtc.AddSeconds((i + 1) * bucketSeconds);
            var rawShares = SummarizeWindow(sortedEvents, bStart, bEnd);
            if (rawShares.Count == 0)
            {
                continue;
            }

            double totalWeight = 0;
            foreach (var s in rawShares)
            {
                totalWeight += s.WeightedTokens;
            }

            var filtered = new List<ModelShare>(3);
            double keptWeight = 0;
            foreach (var s in rawShares)
            {
                if (s.Share >= 0.12 || filtered.Count == 0)
                {
                    filtered.Add(s);
                    keptWeight += s.WeightedTokens;
                    if (filtered.Count == 3)
                    {
                        break;
                    }
                }
            }

            var renormalized = new List<ModelShare>(filtered.Count);
            foreach (var s in filtered)
            {
                var normShare = keptWeight > 0 ? s.WeightedTokens / keptWeight : 1.0 / filtered.Count;
                renormalized.Add(s with { Share = normShare });
            }

            buckets.Add(new ActivityBucket(bStart, bEnd, renormalized, totalWeight));
        }

        return buckets;
    }

    public static IReadOnlyList<DropAnnotation> DetectDrops(
        IReadOnlyList<HistoryPoint> points,
        IReadOnlyList<ModelUsageEvent> sortedEvents,
        bool includeClaude,
        int minDropPercent = 5)
    {
        var annotations = new List<DropAnnotation>();
        if (points.Count < 2 || sortedEvents.Count == 0)
        {
            return annotations;
        }

        DetectSeriesDrops(points, p => p.G5, sortedEvents, minDropPercent, maxPlateauMinutes: 5.0, requireModels: true, annotations);
        if (includeClaude)
        {
            DetectSeriesDrops(points, p => p.C5, sortedEvents, minDropPercent, maxPlateauMinutes: 5.0, requireModels: true, annotations);
        }

        return annotations;
    }

    public static IReadOnlyList<DropAnnotation> DetectDropSpans(
        IReadOnlyList<HistoryPoint> points,
        Func<HistoryPoint, int?> selector,
        IReadOnlyList<ModelUsageEvent> sortedEvents,
        int minDropPercent = 1,
        double maxPlateauMinutes = 6.0)
    {
        var annotations = new List<DropAnnotation>();
        if (points.Count < 2)
        {
            return annotations;
        }

        DetectSeriesDrops(points, selector, sortedEvents, minDropPercent, maxPlateauMinutes, requireModels: false, annotations);
        return annotations;
    }

    private static void DetectSeriesDrops(
        IReadOnlyList<HistoryPoint> points,
        Func<HistoryPoint, int?> selector,
        IReadOnlyList<ModelUsageEvent> sortedEvents,
        int minDropPercent,
        double maxPlateauMinutes,
        bool requireModels,
        List<DropAnnotation> output)
    {
        var series = new List<(DateTimeOffset T, int Val)>(points.Count);
        foreach (var p in points)
        {
            var v = selector(p);
            if (v.HasValue)
            {
                series.Add((p.T, v.Value));
            }
        }

        if (series.Count < 2)
        {
            return;
        }

        var inRun = false;
        var peakTime = series[0].T;
        var peakVal = series[0].Val;
        var troughTime = series[0].T;
        var troughVal = series[0].Val;
        var lastDropTime = series[0].T;

        void FinalizeRun()
        {
            if (!inRun)
            {
                return;
            }

            var drop = peakVal - troughVal;
            if (drop >= minDropPercent)
            {
                var models = sortedEvents.Count > 0
                    ? SummarizeWindow(sortedEvents, peakTime.AddMinutes(-2), troughTime.AddMinutes(1))
                    : [];
                if (!requireModels || models.Count > 0)
                {
                    output.Add(new DropAnnotation(peakTime, troughTime, peakVal, troughVal, drop, models));
                }
            }

            inRun = false;
        }

        for (var i = 1; i < series.Count; i++)
        {
            var prev = series[i - 1];
            var curr = series[i];

            if (curr.Val < prev.Val)
            {
                if (!inRun)
                {
                    inRun = true;
                    peakTime = prev.T;
                    peakVal = prev.Val;
                }

                troughTime = curr.T;
                troughVal = curr.Val;
                lastDropTime = curr.T;
            }
            else if (curr.Val == prev.Val)
            {
                if (inRun && (curr.T - lastDropTime).TotalMinutes > maxPlateauMinutes)
                {
                    FinalizeRun();
                    peakTime = curr.T;
                    peakVal = curr.Val;
                }
            }
            else
            {
                FinalizeRun();
                peakTime = curr.T;
                peakVal = curr.Val;
            }
        }

        FinalizeRun();
    }

    private static int LowerBound(IReadOnlyList<ModelUsageEvent> sortedEvents, DateTimeOffset targetUtc)
    {
        var lo = 0;
        var hi = sortedEvents.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (sortedEvents[mid].TimestampUtc < targetUtc)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
