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
        Assert.False(noReset.HasResetTime);
        Assert.Equal("-", noReset.ResetLocalText);
        Assert.Equal("-", noReset.FormatResetIn(now, AppLanguage.Pl));
        Assert.Null(noReset.GetResetProgressFraction(now));

        var epochReset = new QuotaBucket("Gemini Models", "5h", "5h", 1.0, 100, QuotaStatus.Ok, DateTimeOffset.UnixEpoch);
        Assert.False(epochReset.HasResetTime);
        Assert.Equal("-", epochReset.ResetLocalText);
        Assert.Equal("-", epochReset.FormatResetIn(now, AppLanguage.Pl));
        Assert.Null(epochReset.GetResetProgressFraction(now));

        var unknownWindow = new QuotaBucket("Gemini Models", "daily", "daily", 0.5, 50, QuotaStatus.Ok, now.AddHours(1));
        Assert.Null(unknownWindow.GetResetProgressFraction(now));
    }

    [Fact]
    public async Task RunProcessAsync_RunsSubprocessSilentlyAndCapturesOutput()
    {
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var (exitCode, stdout, stderr) = await AgyQuotaFetcher.RunProcessAsync(
            cmdPath,
            "/c echo hidden-ok",
            _tempDir,
            TimeSpan.FromSeconds(10));

        Assert.Equal(0, exitCode);
        Assert.Contains("hidden-ok", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public async Task RunProcessAsync_SupportsParallelRuns()
    {
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var t1 = AgyQuotaFetcher.RunProcessAsync(cmdPath, "/c echo first-run", _tempDir, TimeSpan.FromSeconds(10));
        var t2 = AgyQuotaFetcher.RunProcessAsync(cmdPath, "/c echo second-run", _tempDir, TimeSpan.FromSeconds(10));

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

    [Fact]
    public void TrayManager_UnpinningVisiblePopup_StartsHoverWatchTimer()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var settings = new AppSettings { OpenOnHover = true };
                var popup = new AntigravityQuotaTray.UI.PopupWindow(settings)
                {
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false
                };

                using var tray = new TrayManager(popup, settings);
                try
                {
                    popup.SetPinned(true);
                    popup.Show();
                    Assert.False(tray.IsHoverWatchActive);

                    var pinEvents = new List<bool>();
                    popup.PinChanged += pinEvents.Add;

                    popup.SetPinned(false);

                    Assert.Equal([false], pinEvents);
                    Assert.True(tray.IsHoverWatchActive);
                }
                finally
                {
                    popup.Close();
                }
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
    }

    [Fact]
    public void ModelNameFormatter_ParsesKnownAndUnknownModels()
    {
        Assert.Equal(("hydrogen", "Hydrogen xHigh", "H-xH"), ModelNameFormatter.Parse("gemini-4-hydrogen-xhigh"));
        Assert.Equal(("hydrogen", "Hydrogen High", "H-Hi"), ModelNameFormatter.Parse("gemini-4-hydrogen-tiered-high"));
        Assert.Equal(("carbon", "Carbon Med", "C-Med"), ModelNameFormatter.Parse("gemini-4-carbon-medium"));
        Assert.Equal(("neon", "Neon Low", "N-Lo"), ModelNameFormatter.Parse("gemini-4-neon-low"));
        Assert.Equal(("argon", "Argon High", "A-Hi"), ModelNameFormatter.Parse("gemini-4-argon-high"));
        Assert.Equal(("orchestrator", "Orch (Neon)", "Orch-N"), ModelNameFormatter.Parse("gemini-pro-orchestrator-neon"));
        Assert.Equal(("flash", "Gemini Flash", "Flash"), ModelNameFormatter.Parse("gemini-3.8-flash"));
        Assert.Equal(("claude-opus", "Opus High", "Opus-Hi"), ModelNameFormatter.Parse("claude-opus-5-5-high"));
        Assert.Equal(("claude-sonnet", "Sonnet Med", "Son-Med"), ModelNameFormatter.Parse("claude-sonnet-5-5-medium"));
        Assert.Equal(("gpt", "GPT", "GPT"), ModelNameFormatter.Parse("gpt-oss-120b"));
        Assert.Equal(("custom-model-v2", "custom-model-v2", "custom"), ModelNameFormatter.Parse("custom-model-v2"));
    }

    [Fact]
    public void ModelActivityTracker_LoadEvents_ParsesIncrementallyAndToleratesMalformedEof()
    {
        var brainRoot = Path.Combine(_tempDir, "brain");
        var convLogs = Path.Combine(brainRoot, "conv-1", ".system_generated", "logs");
        Directory.CreateDirectory(convLogs);
        var transcriptPath = Path.Combine(convLogs, "transcript.jsonl");

        var now = DateTimeOffset.Parse("2026-11-26T16:00:00Z");
        var tOld = now.AddHours(-10).ToString("O");
        var t1 = now.AddMinutes(-20).ToString("O");
        var t2 = now.AddMinutes(-10).ToString("O");
        var t3 = now.AddMinutes(-5).ToString("O");

        var initialContent = string.Join("\n", [
            $$"""{"type":"USER_INPUT","created_at":"{{t1}}","content":"hi"}""",
            $$"""{"type":"PLANNER_RESPONSE","created_at":"{{tOld}}","model":"gemini-4-argon-low","input_tokens":100,"cache_read_tokens":0,"output_tokens":10}""",
            $$"""{"type":"PLANNER_RESPONSE","created_at":"{{t1}}","model":"gemini-4-hydrogen-xhigh","input_tokens":1000,"cache_read_tokens":2000,"output_tokens":500}""",
            $$"""{"type":"PLANNER_RESPONSE","created_at":"{{t2}}","model":"gemini-4-neon""" // truncated mid-write
        ]);
        File.WriteAllText(transcriptPath, initialContent);

        var tracker = new ModelActivityTracker(brainRoot);
        var firstPass = tracker.LoadEvents(TimeSpan.FromHours(1), now);
        Assert.Single(firstPass);
        Assert.Equal("Hydrogen xHigh", firstPass[0].DisplayName);
        Assert.Equal(3200.0, firstPass[0].WeightedTokens, precision: 3); // 1000 + 200 + 2000

        // Complete the truncated line and append another event
        var completion = $$"""-medium","input_tokens":500,"cache_read_tokens":0,"output_tokens":100}""" + "\n" +
                         $$"""{"type":"PLANNER_RESPONSE","created_at":"{{t3}}","model":"claude-opus-5-5-high","input_tokens":200,"cache_read_tokens":0,"output_tokens":50}""" + "\n";
        File.AppendAllText(transcriptPath, completion);

        var secondPass = tracker.LoadEvents(TimeSpan.FromHours(1), now);
        Assert.Equal(3, secondPass.Count);
        Assert.Equal("Hydrogen xHigh", secondPass[0].DisplayName);
        Assert.Equal("Neon Med", secondPass[1].DisplayName);
        Assert.Equal("Opus High", secondPass[2].DisplayName);
    }

    [Fact]
    public void ModelActivityTracker_SummarizeWindowAndDetectDrops_WorkForSingleAndConcurrentModels()
    {
        var t0 = DateTimeOffset.Parse("2026-11-26T15:00:00Z");

        var events = new List<ModelUsageEvent>
        {
            new(t0.AddMinutes(1), "gemini-4-hydrogen-xhigh", "hydrogen", "Hydrogen xHigh", "H-xH", 700, 300, 0, 100),
            new(t0.AddMinutes(2), "gemini-4-neon-medium", "neon", "Neon Med", "N-Med", 300, 100, 0, 50),
            new(t0.AddMinutes(20), "gemini-4-carbon-medium", "carbon", "Carbon Med", "C-Med", 900, 500, 0, 100),
            new(t0.AddMinutes(21), "gemini-4-neon-low", "neon", "Neon Low", "N-Lo", 50, 50, 0, 0)
        };

        var dualShares = ModelActivityTracker.SummarizeWindow(events, t0, t0.AddMinutes(5));
        Assert.Equal(2, dualShares.Count);
        Assert.Equal("Hydrogen xHigh", dualShares[0].DisplayName);
        Assert.Equal(0.7, dualShares[0].Share, precision: 3);
        Assert.Equal("Hydrogen xHigh (70%), Neon Med (30%)", ModelActivityTracker.FormatCompactSummary(dualShares));

        var singleDomShares = ModelActivityTracker.SummarizeWindow(events, t0.AddMinutes(18), t0.AddMinutes(25));
        Assert.Equal("Carbon Med", ModelActivityTracker.FormatCompactSummary(singleDomShares));

        var points = new List<HistoryPoint>
        {
            new(t0, 90, 95, 100, 50, 1000),
            new(t0.AddMinutes(2), 86, 95, 100, 50, 1000),
            new(t0.AddMinutes(4), 81, 94, 100, 50, 1000), // 9% drop from t0
            new(t0.AddMinutes(10), 100, 94, 100, 50, 1000), // reset spike (ignored)
            new(t0.AddMinutes(19), 98, 94, 100, 50, 1000),
            new(t0.AddMinutes(22), 92, 93, 100, 50, 1000) // 8% drop from 100 -> 98 -> 92
        };

        var drops = ModelActivityTracker.DetectDrops(points, events, includeClaude: false, minDropPercent: 5);
        Assert.Equal(2, drops.Count);
        Assert.Equal(90, drops[0].StartPercent);
        Assert.Equal(81, drops[0].EndPercent);
        Assert.Equal(9, drops[0].DropPercent);
        Assert.Equal("Hydrogen xHigh", drops[0].Models[0].DisplayName);

        Assert.Equal(100, drops[1].StartPercent);
        Assert.Equal(92, drops[1].EndPercent);
        Assert.Equal(8, drops[1].DropPercent);
        Assert.Equal("Carbon Med", drops[1].Models[0].DisplayName);
    }

    [Fact]
    public void ModelActivityTracker_DetectDropSpans_BridgesShortPlateausAndTrimsTrailingFlat()
    {
        var t0 = DateTimeOffset.Parse("2026-11-26T18:00:00Z");
        var events = new List<ModelUsageEvent>
        {
            new(t0.AddMinutes(1), "gemini-4-hydrogen-xhigh", "hydrogen", "Hydrogen xHigh", "H-xH", 800, 400, 0, 100)
        };

        var points = new List<HistoryPoint>
        {
            new(t0, 90, 95, 100, 50, 1000),
            new(t0.AddMinutes(2), 88, 95, 100, 50, 1000),
            new(t0.AddMinutes(5), 88, 95, 100, 50, 1000), // 3-minute plateau (bridged)
            new(t0.AddMinutes(6), 84, 95, 100, 50, 1000), // last actual drop
            new(t0.AddMinutes(10), 84, 95, 100, 50, 1000), // trailing flat (trimmed)
            new(t0.AddMinutes(15), 84, 95, 100, 50, 1000), // exceeds 6m plateau -> closes span
            new(t0.AddMinutes(30), 82, 95, 100, 50, 1000)  // 2% drop without local transcript logs
        };

        var spans = ModelActivityTracker.DetectDropSpans(points, p => p.G5, events, minDropPercent: 1, maxPlateauMinutes: 6.0);
        Assert.Equal(2, spans.Count);

        Assert.Equal(t0, spans[0].StartUtc);
        Assert.Equal(t0.AddMinutes(6), spans[0].EndUtc);
        Assert.Equal(90, spans[0].StartPercent);
        Assert.Equal(84, spans[0].EndPercent);
        Assert.Equal(6, spans[0].DropPercent);
        Assert.Single(spans[0].Models);
        Assert.Equal("Hydrogen xHigh", spans[0].Models[0].DisplayName);

        Assert.Equal(t0.AddMinutes(15), spans[1].StartUtc);
        Assert.Equal(t0.AddMinutes(30), spans[1].EndUtc);
        Assert.Equal(2, spans[1].DropPercent);
        Assert.Empty(spans[1].Models);
    }

    [Fact]
    public void ModelNameFormatter_ColorsAreDistinctForCarbonHydrogenNeonAndWeekly()
    {
        var neon = ModelNameFormatter.GetFamilyRgb("neon");
        var carbon = ModelNameFormatter.GetFamilyRgb("carbon");
        var hydrogen = ModelNameFormatter.GetFamilyRgb("hydrogen");
        var gemini5h = (R: (byte)56, G: (byte)189, B: (byte)248);
        var weekly = (R: (byte)52, G: (byte)211, B: (byte)153);

        Assert.True(hydrogen.R > 200 && hydrogen.G > 180 && hydrogen.B < 60, $"Hydrogen should be yellow, got {hydrogen}");
        Assert.True(carbon.R > 220 && carbon.G < 130 && carbon.B > 120, $"Carbon should be warm pink, got {carbon}");
        Assert.True(neon.R > 140 && neon.G < 110 && neon.B > 220, $"Neon should be electric violet, got {neon}");
        Assert.True(ColorDistance(carbon, hydrogen) > 200, "Carbon and Hydrogen should have distinct colors");
        Assert.True(ColorDistance(carbon, neon) > 200, "Carbon and Neon should have distinct colors");
        Assert.True(ColorDistance(neon, gemini5h) > 200, "Neon should be distinct from sky-blue Gemini 5h line");
        Assert.True(ColorDistance(neon, weekly) > 200, "Neon should be distinct from green Weekly line");
    }

    [Fact]
    public void App_ShouldAutoRefreshOnPopupOpen_RespectsCooldownAndFetchingState()
    {
        var now = DateTimeOffset.Parse("2026-11-27T00:00:00Z");

        Assert.True(App.ShouldAutoRefreshOnPopupOpen(isFetching: false, DateTimeOffset.MinValue, now));
        Assert.False(App.ShouldAutoRefreshOnPopupOpen(isFetching: false, now.AddSeconds(-5), now));
        Assert.False(App.ShouldAutoRefreshOnPopupOpen(isFetching: false, now.AddSeconds(-14.9), now));
        Assert.True(App.ShouldAutoRefreshOnPopupOpen(isFetching: false, now.AddSeconds(-15.0), now));
        Assert.False(App.ShouldAutoRefreshOnPopupOpen(isFetching: true, now.AddMinutes(-5), now));
    }

    [Fact]
    public void HistoryChartControl_ToggleModelBreakdown_TogglesStateAndRaisesEvent()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var chart = new AntigravityQuotaTray.UI.HistoryChartControl();
                var events = new List<bool>();
                chart.ModelBreakdownToggled += events.Add;

                Assert.False(chart.ShowModelBreakdownOnChart);
                chart.ToggleModelBreakdown();
                Assert.True(chart.ShowModelBreakdownOnChart);
                chart.ToggleModelBreakdown();
                Assert.False(chart.ShowModelBreakdownOnChart);
                Assert.Equal([true, false], events);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
    }

    private static int ColorDistance((byte R, byte G, byte B) a, (byte R, byte G, byte B) b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

    [Fact]
    public void HistoryStore_PersistsModelsAndReadsLegacyLines()
    {
        var store = new HistoryStore(_tempDir);
        var t1 = DateTimeOffset.Parse("2026-11-26T15:00:00Z");
        var t2 = t1.AddMinutes(5);

        File.WriteAllLines(store.FilePath, [
            $$"""{"t":"{{t1:O}}","g5":80,"gw":90,"c5":100,"cw":50,"cr":2000}"""
        ]);

        store.Append(CreateSnapshot(t2, 74, 89, 100, 50, 2000), "Hydrogen xHigh (70%), Neon Med (30%)");

        var loaded = store.LoadRange(TimeSpan.FromHours(1), t2.AddMinutes(1));
        Assert.Equal(2, loaded.Count);
        Assert.Null(loaded[0].Models);
        Assert.Equal("Hydrogen xHigh (70%), Neon Med (30%)", loaded[1].Models);
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
