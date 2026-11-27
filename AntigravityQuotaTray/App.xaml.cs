using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AntigravityQuotaTray.Models;
using AntigravityQuotaTray.Services;
using AntigravityQuotaTray.Tray;
using AntigravityQuotaTray.UI;

namespace AntigravityQuotaTray;

public partial class App : Application
{
    private const string MutexName = @"Local\AntigravityQuotaTray_SingleInstance";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private AppLogger? _logger;
    private SettingsStore? _settingsStore;
    private HistoryStore? _historyStore;
    private ModelActivityTracker? _modelActivityTracker;
    private AutostartManager? _autostartManager;
    private AgyQuotaFetcher? _fetcher;
    private PopupWindow? _popup;
    private TrayManager? _trayManager;
    private DispatcherTimer? _pollTimer;

    internal static readonly TimeSpan AutoRefreshCooldown = TimeSpan.FromSeconds(15);

    private AppSettings _settings = new();
    private QuotaSnapshot? _latestSnapshot;
    private bool _isFetching;
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPruneUtc = DateTimeOffset.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        _logger = new AppLogger();
        RegisterGlobalExceptionHandlers();
        _logger.Info($"Starting AntigravityQuotaTray (exe: {Environment.ProcessPath ?? "unknown"})");

        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            _logger.Info("Another instance is already running (tray icon active). Exiting secondary instance.");
            Shutdown();
            return;
        }

        try
        {
            _settingsStore = new SettingsStore();
            _historyStore = new HistoryStore();
            _modelActivityTracker = new ModelActivityTracker(logger: _logger);
            _autostartManager = new AutostartManager();
            _fetcher = new AgyQuotaFetcher(logger: _logger);

            _settings = _settingsStore.Load();
            if (!System.IO.File.Exists(_settingsStore.FilePath))
            {
                _settingsStore.Save(_settings);
            }

            _logger.Info($"Settings loaded: poll={_settings.PollIntervalMinutes}m, style={_settings.TrayIconStyle}, hover={_settings.OpenOnHover}, autostart={_settings.AutostartEnabled}, range={_settings.ChartRangeHours}h, lang={_settings.Language}");

            try
            {
                _autostartManager.SetEnabled(_settings.AutostartEnabled);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to update Windows autostart registry entry.", ex);
            }

            _historyStore.PruneOlderThan(TimeSpan.FromDays(30));
            _lastPruneUtc = DateTimeOffset.UtcNow;

            _popup = new PopupWindow(_settings);
            _trayManager = new TrayManager(_popup, _settings);

            WireEvents();

            var initialHistory = _historyStore.LoadRange(TimeSpan.FromHours(_settings.ChartRangeHours));
            var initialModelEvents = _modelActivityTracker.LoadEvents(TimeSpan.FromHours(_settings.ChartRangeHours));
            _popup.UpdateView(null, initialHistory, _settings, initialModelEvents);

            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PollIntervalMinutes))
            };
            _pollTimer.Tick += async (_, _) => await RefreshQuotaAsync();
            _pollTimer.Start();

            _logger.Info($"Startup complete. Tray icon initialized (history points: {initialHistory.Count}). Triggering initial quota fetch.");
            _ = RefreshQuotaAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("Fatal error during application startup.", ex);
            Shutdown(1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.Error("Unhandled WPF Dispatcher exception.", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            _logger?.Error($"Unhandled AppDomain exception (IsTerminating={args.IsTerminating}).", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("Unobserved Task exception.", args.Exception);
            args.SetObserved();
        };
    }

    private void WireEvents()
    {
        if (_popup is null || _trayManager is null)
        {
            return;
        }

        _popup.RefreshRequested += async () => await RefreshQuotaAsync();
        _popup.SettingsChanged += OnSettingsChanged;
        _popup.ChartRangeChanged += OnChartRangeChanged;
        _popup.OpenDataFolderRequested += OpenDataFolder;
        _popup.ExitRequested += ShutdownApp;

        _trayManager.RefreshRequested += async () => await RefreshQuotaAsync();
        _trayManager.PopupOpening += OnPopupOpening;
        _trayManager.AutostartToggledFromMenu += OnAutostartToggledFromMenu;
        _trayManager.OpenDataFolderRequested += OpenDataFolder;
        _trayManager.ExitRequested += ShutdownApp;
    }

    internal static bool ShouldAutoRefreshOnPopupOpen(bool isFetching, DateTimeOffset lastRefreshUtc, DateTimeOffset nowUtc) =>
        !isFetching && (nowUtc - lastRefreshUtc) >= AutoRefreshCooldown;

    private void OnPopupOpening()
    {
        if (ShouldAutoRefreshOnPopupOpen(_isFetching, _lastRefreshUtc, DateTimeOffset.UtcNow))
        {
            _ = RefreshQuotaAsync();
        }
    }

    private async Task RefreshQuotaAsync()
    {
        if (_isFetching || _fetcher is null || _historyStore is null || _popup is null || _trayManager is null)
        {
            return;
        }

        _isFetching = true;
        _popup.SetLoading(true);

        try
        {
            var fresh = await _fetcher.FetchAsync(_settings.Language);
            var pollWindow = TimeSpan.FromMinutes(Math.Max(2, _settings.PollIntervalMinutes + 1));
            var chartWindow = TimeSpan.FromHours(_settings.ChartRangeHours);
            var fetchWindow = chartWindow > pollWindow ? chartWindow : pollWindow;
            var modelEvents = _modelActivityTracker is not null
                ? await Task.Run(() => _modelActivityTracker.LoadEvents(fetchWindow))
                : [];

            if (string.IsNullOrWhiteSpace(fresh.ErrorMessage))
            {
                _latestSnapshot = fresh;
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                var windowStart = fresh.TimestampUtc - pollWindow;
                var activeShares = ModelActivityTracker.SummarizeWindow(modelEvents, windowStart, fresh.TimestampUtc);
                var modelsSummary = ModelActivityTracker.FormatCompactSummary(activeShares);
                _historyStore.Append(fresh, modelsSummary);
                _logger?.Info(
                    $"Quota updated: Gemini 5h={fresh.Gemini5h?.RemainingPercent.ToString() ?? "--"}%, " +
                    $"Gemini W={fresh.GeminiWeekly?.RemainingPercent.ToString() ?? "--"}%, " +
                    $"Claude 5h={fresh.Claude5h?.RemainingPercent.ToString() ?? "--"}%, " +
                    $"Claude W={fresh.ClaudeWeekly?.RemainingPercent.ToString() ?? "--"}%, " +
                    $"Credits={fresh.RemainingCredits?.ToString() ?? "--"}");

                if ((DateTimeOffset.UtcNow - _lastPruneUtc).TotalHours >= 24)
                {
                    _historyStore.PruneOlderThan(TimeSpan.FromDays(30));
                    _lastPruneUtc = DateTimeOffset.UtcNow;
                }
            }
            else if (_latestSnapshot is not null)
            {
                _logger?.Warn($"Quota refresh failed (keeping last snapshot): {fresh.ErrorMessage}");
                // Preserve last known readings while surfacing the latest error
                _latestSnapshot = _latestSnapshot with
                {
                    TimestampUtc = fresh.TimestampUtc,
                    ErrorMessage = fresh.ErrorMessage
                };
            }
            else
            {
                _logger?.Warn($"Quota refresh failed (no previous snapshot): {fresh.ErrorMessage}");
                _latestSnapshot = fresh;
            }

            var history = _historyStore.LoadRange(chartWindow);
            _popup.UpdateView(_latestSnapshot, history, _settings, modelEvents);
            _trayManager.UpdateDisplay(_latestSnapshot, _settings);
        }
        catch (Exception ex)
        {
            _logger?.Error("Unexpected error during RefreshQuotaAsync.", ex);
        }
        finally
        {
            _isFetching = false;
            _popup.SetLoading(false);
            if (_pollTimer is not null && _pollTimer.IsEnabled)
            {
                _pollTimer.Stop();
                _pollTimer.Start();
            }
        }
    }

    private void OnSettingsChanged(AppSettings updated)
    {
        _settings = updated;
        _settingsStore?.Save(updated);

        try
        {
            _autostartManager?.SetEnabled(updated.AutostartEnabled);
        }
        catch (Exception ex)
        {
            _logger?.Warn("Failed to update Windows autostart registry entry.", ex);
        }

        if (_pollTimer is not null)
        {
            var targetInterval = TimeSpan.FromMinutes(Math.Max(1, updated.PollIntervalMinutes));
            if (_pollTimer.Interval != targetInterval)
            {
                _pollTimer.Interval = targetInterval;
            }
        }

        _trayManager?.UpdateDisplay(_latestSnapshot, updated);
    }

    private void OnAutostartToggledFromMenu(bool enabled)
    {
        _settings.AutostartEnabled = enabled;
        OnSettingsChanged(_settings);
        if (_popup is not null && _historyStore is not null)
        {
            var window = TimeSpan.FromHours(_settings.ChartRangeHours);
            var history = _historyStore.LoadRange(window);
            var modelEvents = _modelActivityTracker?.LoadEvents(window) ?? [];
            _popup.UpdateView(_latestSnapshot, history, _settings, modelEvents);
        }
    }

    private async void OnChartRangeChanged(int hours)
    {
        if (_historyStore is null || _popup is null)
        {
            return;
        }

        try
        {
            var window = TimeSpan.FromHours(hours);
            var history = _historyStore.LoadRange(window);
            var modelEvents = _modelActivityTracker is not null
                ? await Task.Run(() => _modelActivityTracker.LoadEvents(window))
                : [];
            _popup.UpdateChartOnly(history, hours, _settings.ShowClaudeOnChart, _settings.Language, modelEvents);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to update chart range to {hours}h.", ex);
        }
    }

    private void OpenDataFolder()
    {
        if (_settingsStore is null)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(_settingsStore.DirectoryPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_settingsStore.DirectoryPath}\"",
            UseShellExecute = true
        });
    }

    private void ShutdownApp()
    {
        _logger?.Info("Shutdown requested by user.");
        _pollTimer?.Stop();
        _trayManager?.Dispose();
        _popup?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info($"Application exiting (code={e.ApplicationExitCode}, ownsMutex={_ownsMutex}).");
        _pollTimer?.Stop();
        _trayManager?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (Exception ex)
            {
                _logger?.Warn("Failed to release single-instance mutex.", ex);
            }
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
