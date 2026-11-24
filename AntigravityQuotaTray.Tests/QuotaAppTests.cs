using System.IO;
using AntigravityQuotaTray.Models;
using AntigravityQuotaTray.Services;
using AntigravityQuotaTray.Tray;

namespace AntigravityQuotaTray.Tests;

public sealed class QuotaAppTests : IDisposable
{
    private readonly string _tempDir;

    public QuotaAppTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AntigravityQuotaTrayTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock
        }
    }

    [Fact]
    public void ParseResponses_ParsesUsageAndCreditsJsonCorrectly()
    {
        const string usageJson = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "usage",
            "data": {
              "groups": [
                {
                  "name": "Gemini Models",
                  "buckets": [
                    {
                      "id": "gemini-weekly",
                      "name": "Weekly Limit Remaining",
                      "window": "weekly",
                      "remaining_fraction": 0.6193,
                      "reset_time": "2026-11-30T20:06:21Z"
                    },
                    {
                      "id": "gemini-5h",
                      "name": "Five Hour Limit Remaining",
                      "window": "5h",
                      "remaining_fraction": 0.874,
                      "reset_time": "2026-11-24T20:53:30Z"
                    }
                  ]
                },
                {
                  "name": "Claude and GPT models",
                  "buckets": [
                    {
                      "id": "3p-weekly",
                      "name": "Weekly Limit Remaining",
                      "window": "weekly",
                      "remaining_fraction": 0.032,
                      "reset_time": "2026-12-01T01:04:04Z"
                    },
                    {
                      "id": "3p-5h",
                      "name": "Five Hour Limit Remaining",
                      "window": "5h",
                      "remaining_fraction": 0.25,
                      "reset_time": "2026-11-24T20:50:37Z"
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

        const string creditsJson = """
        {
          "status": "SUCCESS",
          "command": {
            "name": "credits",
            "data": {
              "remaining_credits": 1000,
              "upgrade_uri": "https://antigravity.google/g1-upgrade"
            }
          }
        }
        """;

        var now = DateTimeOffset.Parse("2026-11-24T18:35:00Z");
        var snap = AgyQuotaFetcher.ParseResponses(usageJson, creditsJson, true, now);

        Assert.Null(snap.ErrorMessage);
        Assert.NotNull(snap.Gemini5h);
        Assert.NotNull(snap.GeminiWeekly);
        Assert.NotNull(snap.Claude5h);
        Assert.NotNull(snap.ClaudeWeekly);

        Assert.Equal(87, snap.Gemini5h!.RemainingPercent);
        Assert.Equal(QuotaStatus.Ok, snap.Gemini5h.Status);
        Assert.Equal(62, snap.GeminiWeekly!.RemainingPercent);
        Assert.Equal(QuotaStatus.Ok, snap.GeminiWeekly.Status);

        Assert.Equal(25, snap.Claude5h!.RemainingPercent);
        Assert.Equal(QuotaStatus.Moderate, snap.Claude5h.Status);
        Assert.Equal(3, snap.ClaudeWeekly!.RemainingPercent);
        Assert.Equal(QuotaStatus.Low, snap.ClaudeWeekly.Status);

        Assert.Equal(1000, snap.RemainingCredits);
        Assert.True(snap.UseAiCreditsEnabled);
    }

    [Fact]
    public void ParseResponses_HandlesInvalidJsonGracefully()
    {
        var now = DateTimeOffset.UtcNow;
        var snap = AgyQuotaFetcher.ParseResponses("{ invalid json", null, false, now);

        Assert.NotNull(snap.ErrorMessage);
        Assert.Null(snap.Gemini5h);
        Assert.False(snap.UseAiCreditsEnabled);
    }

    [Fact]
    public void HistoryStore_AppendLoadRangeAndPrune()
    {
        var store = new HistoryStore(_tempDir);
        var now = DateTimeOffset.Parse("2026-11-24T19:00:00Z");

        var oldSnap = CreateSnapshot(now.AddDays(-35), 90, 80, 50, 40, 900);
        var midSnap = CreateSnapshot(now.AddHours(-10), 75, 65, 20, 10, 950);
        var recentSnap = CreateSnapshot(now.AddMinutes(-30), 87, 61, 7, 3, 1000);

        store.Append(oldSnap);
        store.Append(midSnap);
        store.Append(recentSnap);

        var last6h = store.LoadRange(TimeSpan.FromHours(6), now);
        Assert.Single(last6h);
        Assert.Equal(87, last6h[0].G5);

        var last24h = store.LoadRange(TimeSpan.FromHours(24), now);
        Assert.Equal(2, last24h.Count);
        Assert.Equal(75, last24h[0].G5);
        Assert.Equal(87, last24h[1].G5);

        store.PruneOlderThan(TimeSpan.FromDays(30), now);
        var allAfterPrune = store.LoadRange(TimeSpan.FromDays(100), now);
        Assert.Equal(2, allAfterPrune.Count);
    }

    [Fact]
    public void SettingsStore_DefaultAndRoundTrip()
    {
        var store = new SettingsStore(_tempDir);
        var defaults = store.Load();

        Assert.Equal(1, defaults.PollIntervalMinutes);
        Assert.Equal(TrayIconStyle.NumberWithWeeklyBar, defaults.TrayIconStyle);
        Assert.True(defaults.OpenOnHover);
        Assert.True(defaults.AutostartEnabled);
        Assert.Equal(AppLanguage.En, defaults.Language);

        defaults.PollIntervalMinutes = 5;
        defaults.TrayIconStyle = TrayIconStyle.RingGauge;
        defaults.OpenOnHover = false;
        defaults.AutostartEnabled = false;
        defaults.ChartRangeHours = 168;
        defaults.ShowClaudeOnChart = true;
        defaults.Language = AppLanguage.Pl;
        store.Save(defaults);

        var reloaded = store.Load();
        Assert.Equal(5, reloaded.PollIntervalMinutes);
        Assert.Equal(TrayIconStyle.RingGauge, reloaded.TrayIconStyle);
        Assert.False(reloaded.OpenOnHover);
        Assert.False(reloaded.AutostartEnabled);
        Assert.Equal(168, reloaded.ChartRangeHours);
        Assert.True(reloaded.ShowClaudeOnChart);
        Assert.Equal(AppLanguage.Pl, reloaded.Language);
    }

    [Fact]
    public void TrayIconRenderer_RendersAllStylesAndStates()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshots = new QuotaSnapshot?[]
        {
            null,
            CreateSnapshot(now, 100, 80, 50, 50, 1000),
            CreateSnapshot(now, 35, 25, 10, 5, 3000),
            CreateSnapshot(now, 0, 12, 0, 0, 0),
            QuotaSnapshot.Failure(now, "Timeout")
        };

        foreach (var style in Enum.GetValues<TrayIconStyle>())
        {
            foreach (var snap in snapshots)
            {
                using var icon = TrayIconRenderer.RenderGeminiIcon(snap, style);
                Assert.NotNull(icon);
                Assert.Equal(32, icon.Width);
                Assert.Equal(32, icon.Height);

                var tooltip = TrayIconRenderer.BuildTooltipText(snap);
                Assert.False(string.IsNullOrWhiteSpace(tooltip));
                Assert.True(tooltip.Length <= 63, $"Tooltip exceeded 63 chars: {tooltip}");
                Assert.DoesNotContain("Claude", tooltip, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void AppLogger_WritesEntriesAndRotatesWhenExceedingLimit()
    {
        var logger = new AppLogger(_tempDir, maxBytes: 4096);

        logger.Info("Startup test message");
        logger.Error("Failure test message", new InvalidOperationException("Boom"));

        Assert.True(File.Exists(logger.FilePath));
        var content = File.ReadAllText(logger.FilePath);
        Assert.Contains("[INFO]", content);
        Assert.Contains("Startup test message", content);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("InvalidOperationException: Boom", content);

        var payload = new string('A', 600);
        for (var i = 0; i < 10; i++)
        {
            logger.Info($"Bulk line {i}: {payload}");
        }

        Assert.True(File.Exists(logger.ArchiveFilePath));
        Assert.True(File.Exists(logger.FilePath));
    }

    [Fact]
    public void Localization_SupportsEnglishDefaultAndPolish()
    {
        var now = DateTimeOffset.UtcNow;
        var snap = CreateSnapshot(now, 80, 60, 20, 10, 1000);

        Assert.Equal("Gemini Quota · 5h: 80% · Wk: 60%", TrayIconRenderer.BuildTooltipText(snap, AppLanguage.En));
        Assert.Equal("Gemini Quota · 5h: 80% · Tyg: 60%", TrayIconRenderer.BuildTooltipText(snap, AppLanguage.Pl));
        Assert.Equal("Antigravity Gemini: loading...", TrayIconRenderer.BuildTooltipText(null, AppLanguage.En));
        Assert.Equal("Antigravity Gemini: ładowanie...", TrayIconRenderer.BuildTooltipText(null, AppLanguage.Pl));

        var dueBucket = new QuotaBucket("Gemini Models", "5h", "5h", 0.5, 50, QuotaStatus.Ok, now.AddSeconds(-5));
        Assert.Equal("now", dueBucket.FormatResetIn(now, AppLanguage.En));
        Assert.Equal("teraz", dueBucket.FormatResetIn(now, AppLanguage.Pl));
    }

    [Fact]
    public void QuotaBucket_GetResetProgressFraction_CalculatesRisingProgressCorrectly()
    {
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

        var half5h = new QuotaBucket("Gemini Models", "5h", "5h", 0.5, 50, QuotaStatus.Ok, now.AddHours(2.5));
        Assert.Equal(0.5, half5h.GetResetProgressFraction(now)!.Value, precision: 3);

        var near5h = new QuotaBucket("Gemini Models", "5h", "5h", 0.1, 10, QuotaStatus.Low, now.AddMinutes(30));
        Assert.Equal(0.9, near5h.GetResetProgressFraction(now)!.Value, precision: 3);

        var due5h = new QuotaBucket("Gemini Models", "5h", "5h", 0.0, 0, QuotaStatus.Low, now.AddSeconds(-10));
        Assert.Equal(1.0, due5h.GetResetProgressFraction(now)!.Value, precision: 3);

        var clampedLow5h = new QuotaBucket("Gemini Models", "5h", "5h", 1.0, 100, QuotaStatus.Ok, now.AddHours(6));
        Assert.Equal(0.0, clampedLow5h.GetResetProgressFraction(now)!.Value, precision: 3);

        var halfWeekly = new QuotaBucket("Gemini Models", "weekly", "weekly", 0.5, 50, QuotaStatus.Ok, now.AddDays(3.5));
        Assert.Equal(0.5, halfWeekly.GetResetProgressFraction(now)!.Value, precision: 3);

        var noReset = new QuotaBucket("Gemini Models", "5h", "5h", 0.5, 50, QuotaStatus.Ok, DateTimeOffset.MinValue);
        Assert.Null(noReset.GetResetProgressFraction(now));

        var unknownWindow = new QuotaBucket("Gemini Models", "daily", "daily", 0.5, 50, QuotaStatus.Ok, now.AddHours(1));
        Assert.Null(unknownWindow.GetResetProgressFraction(now));
    }

    [Fact]
    public async Task HiddenProcessRunner_RunsSubprocessAndCapturesOutput()
    {
        using var runner = new HiddenProcessRunner();
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var (exitCode, stdout, stderr) = await runner.RunAsync(
            cmdPath,
            "/c echo hidden-ok",
            _tempDir,
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Contains("hidden-ok", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public async Task HiddenProcessRunner_SupportsParallelRunsWithoutHandleLeaks()
    {
        using var runner = new HiddenProcessRunner();
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var t1 = runner.RunAsync(cmdPath, "/c echo first-run", _tempDir, TimeSpan.FromSeconds(10));
        var t2 = runner.RunAsync(cmdPath, "/c echo second-run", _tempDir, TimeSpan.FromSeconds(10));

        var results = await Task.WhenAll(t1, t2);
        Assert.Equal(0, results[0].ExitCode);
        Assert.Contains("first-run", results[0].Stdout);
        Assert.Equal(0, results[1].ExitCode);
        Assert.Contains("second-run", results[1].Stdout);
    }

    [Fact]
    public void DarkMenuRenderer_SelectedMenuItem_RendersDarkHoverHighlight()
    {
        var renderer = new TrayManager.DarkMenuRenderer();
        using var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = renderer,
            BackColor = TrayManager.DarkMenuRenderer.BgColor,
            ForeColor = TrayManager.DarkMenuRenderer.TextColor,
            ShowImageMargin = false
        };

        var hoveredItem = new System.Windows.Forms.ToolStripMenuItem("Refresh now");
        var normalItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        menu.Items.Add(hoveredItem);
        menu.Items.Add(normalItem);
        hoveredItem.Select();

        using var hoveredBmp = new System.Drawing.Bitmap(Math.Max(180, hoveredItem.Width), Math.Max(26, hoveredItem.Height));
        using (var g = System.Drawing.Graphics.FromImage(hoveredBmp))
        {
            hoveredItem.Size = hoveredBmp.Size;
            renderer.DrawMenuItemBackground(new System.Windows.Forms.ToolStripItemRenderEventArgs(g, hoveredItem));
        }

        var hoveredPixel = hoveredBmp.GetPixel(hoveredBmp.Width / 2, hoveredBmp.Height / 2);
        Assert.Equal(TrayManager.DarkMenuRenderer.HoverColor.ToArgb(), hoveredPixel.ToArgb());

        using var normalBmp = new System.Drawing.Bitmap(Math.Max(180, normalItem.Width), Math.Max(26, normalItem.Height));
        using (var g = System.Drawing.Graphics.FromImage(normalBmp))
        {
            normalItem.Size = normalBmp.Size;
            renderer.DrawMenuItemBackground(new System.Windows.Forms.ToolStripItemRenderEventArgs(g, normalItem));
        }

        var normalPixel = normalBmp.GetPixel(normalBmp.Width / 2, normalBmp.Height / 2);
        Assert.Equal(TrayManager.DarkMenuRenderer.BgColor.ToArgb(), normalPixel.ToArgb());
    }

    private static QuotaSnapshot CreateSnapshot(
        DateTimeOffset ts,
        int g5,
        int gw,
        int c5,
        int cw,
        int credits) =>
        new(
            ts,
            new QuotaBucket("Gemini Models", "Five Hour Limit Remaining", "5h", g5 / 100.0, g5, QuotaSnapshot.StatusFromPercent(g5), ts.AddHours(3)),
            new QuotaBucket("Gemini Models", "Weekly Limit Remaining", "weekly", gw / 100.0, gw, QuotaSnapshot.StatusFromPercent(gw), ts.AddDays(5)),
            new QuotaBucket("Claude and GPT models", "Five Hour Limit Remaining", "5h", c5 / 100.0, c5, QuotaSnapshot.StatusFromPercent(c5), ts.AddHours(3)),
            new QuotaBucket("Claude and GPT models", "Weekly Limit Remaining", "weekly", cw / 100.0, cw, QuotaSnapshot.StatusFromPercent(cw), ts.AddDays(5)),
            credits,
            true,
            null);
}
