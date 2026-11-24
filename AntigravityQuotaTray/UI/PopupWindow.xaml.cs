using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AntigravityQuotaTray.Models;
using AntigravityQuotaTray.Services;

namespace AntigravityQuotaTray.UI;

public partial class PopupWindow : Window
{
    private const double ProgressTrackWidth = 342.0;

    private static readonly SolidColorBrush OkBrush = CreateFrozen(Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush ModerateBrush = CreateFrozen(Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush LowBrush = CreateFrozen(Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush UnknownBrush = CreateFrozen(Color.FromRgb(148, 163, 184));
    private static readonly SolidColorBrush ActiveRangeBg = CreateFrozen(Color.FromRgb(37, 99, 235));
    private static readonly SolidColorBrush InactiveRangeBg = CreateFrozen(Color.FromRgb(39, 39, 42));
    private static readonly SolidColorBrush ActiveRangeFg = CreateFrozen(Color.FromRgb(255, 255, 255));
    private static readonly SolidColorBrush InactiveRangeFg = CreateFrozen(Color.FromRgb(161, 161, 170));

    private AppSettings _settings;
    private QuotaSnapshot? _snapshot;
    private IReadOnlyList<HistoryPoint> _historyPoints = [];
    private bool _isLoading;
    private bool _suppressSettingEvents;

    public bool IsPinned { get; private set; }
    public bool IsSettingsOpen => SettingsPanel.Visibility == Visibility.Visible;

    public event Action? RefreshRequested;
    public event Action<AppSettings>? SettingsChanged;
    public event Action<int>? ChartRangeChanged;
    public event Action? OpenDataFolderRequested;
    public event Action? ExitRequested;

    public PopupWindow(AppSettings initialSettings)
    {
        InitializeComponent();
        _settings = initialSettings;
        ApplySettingsToControls(initialSettings);
        SizeChanged += OnPopupSizeChanged;
    }

    public void UpdateMaxHeightForWorkArea(double workHeightDip)
    {
        MaxHeight = Math.Max(300.0, workHeightDip - 20.0);
    }

    public void EnsureWithinWorkingArea()
    {
        if (!IsVisible || Left < -5000 || Top < -5000)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        var centerPxX = (int)Math.Round((Left + (ActualWidth / 2.0)) * scaleX);
        var centerPxY = (int)Math.Round((Top + (ActualHeight / 2.0)) * scaleY);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(centerPxX, centerPxY));
        var workArea = screen.WorkingArea;

        var workLeft = workArea.Left / scaleX;
        var workTop = workArea.Top / scaleY;
        var workRight = workArea.Right / scaleX;
        var workBottom = workArea.Bottom / scaleY;
        const double margin = 10.0;

        UpdateMaxHeightForWorkArea(workBottom - workTop);

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 480;

        if (Left + width > workRight - margin)
        {
            Left = Math.Max(workLeft + margin, workRight - width - margin);
        }
        else if (Left < workLeft + margin)
        {
            Left = workLeft + margin;
        }

        if (Top + height > workBottom - margin)
        {
            Top = Math.Max(workTop + margin, workBottom - height - margin);
        }
        else if (Top < workTop + margin)
        {
            Top = workTop + margin;
        }
    }

    private void OnPopupSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && IsVisible)
        {
            EnsureWithinWorkingArea();
        }
    }

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (!IsPinned)
        {
            SetPinned(true);
        }

        try
        {
            DragMove();
            EnsureWithinWorkingArea();
        }
        catch (InvalidOperationException)
        {
            // Ignore if mouse button was released before DragMove started
        }
    }

    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;
        PinButton.Background = pinned
            ? CreateFrozen(Color.FromRgb(37, 99, 235))
            : CreateFrozen(Color.FromRgb(39, 39, 42));
    }

    public void SetLoading(bool isLoading)
    {
        _isLoading = isLoading;
        RefreshButton.IsEnabled = !isLoading;
        UpdateHeaderStatus();
    }

    public void UpdateView(QuotaSnapshot? snapshot, IReadOnlyList<HistoryPoint> historyPoints, AppSettings settings)
    {
        _snapshot = snapshot;
        _historyPoints = historyPoints;
        _settings = settings;

        ApplySettingsToControls(settings);
        UpdateHeaderStatus();

        if (snapshot is not null && !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            ErrorBanner.Visibility = Visibility.Visible;
            ErrorBannerText.Text = snapshot.ErrorMessage;
        }
        else
        {
            ErrorBanner.Visibility = Visibility.Collapsed;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var strings = AppStrings.For(_settings.Language);
        ApplyBucket(snapshot?.Gemini5h, Gemini5hPctText, Gemini5hResetText, Gemini5hBarFill, nowUtc, _settings.Language, strings);
        ApplyBucket(snapshot?.GeminiWeekly, GeminiWeeklyPctText, GeminiWeeklyResetText, GeminiWeeklyBarFill, nowUtc, _settings.Language, strings);
        ApplyBucket(snapshot?.Claude5h, Claude5hPctText, Claude5hResetText, Claude5hBarFill, nowUtc, _settings.Language, strings);
        ApplyBucket(snapshot?.ClaudeWeekly, ClaudeWeeklyPctText, ClaudeWeeklyResetText, ClaudeWeeklyBarFill, nowUtc, _settings.Language, strings);

        if (snapshot?.RemainingCredits is int credits)
        {
            CreditsValueText.Text = credits.ToString("N0", CultureInfo.GetCultureInfo(strings.NumberLocale));
        }
        else
        {
            CreditsValueText.Text = "--";
        }

        if (snapshot?.UseAiCreditsEnabled is bool enabled)
        {
            UseAiCreditsText.Text = enabled ? strings.UseAiCreditsOn : strings.UseAiCreditsOff;
            UseAiCreditsBadge.Background = enabled
                ? CreateFrozen(Color.FromRgb(20, 48, 35))
                : CreateFrozen(Color.FromRgb(39, 39, 42));
            UseAiCreditsText.Foreground = enabled ? OkBrush : InactiveRangeFg;
        }
        else
        {
            UseAiCreditsText.Text = strings.UseAiCreditsUnknown;
            UseAiCreditsBadge.Background = CreateFrozen(Color.FromRgb(39, 39, 42));
            UseAiCreditsText.Foreground = InactiveRangeFg;
        }

        HistoryChart.SetData(_historyPoints, _settings.ChartRangeHours, _settings.ShowClaudeOnChart, _settings.Language);
    }

    public void UpdateChartOnly(IReadOnlyList<HistoryPoint> historyPoints, int rangeHours, bool showClaude, AppLanguage language = AppLanguage.En)
    {
        _historyPoints = historyPoints;
        HistoryChart.SetData(historyPoints, rangeHours, showClaude, language);
    }

    private void UpdateHeaderStatus()
    {
        var strings = AppStrings.For(_settings.Language);
        if (_isLoading)
        {
            HeaderStatusDot.Fill = ModerateBrush;
            LastUpdatedText.Text = strings.StatusRefreshing;
            return;
        }

        if (_snapshot is null)
        {
            HeaderStatusDot.Fill = UnknownBrush;
            LastUpdatedText.Text = strings.StatusWaiting;
            return;
        }

        var worstGemini = GetWorstGeminiStatus(_snapshot);
        HeaderStatusDot.Fill = !string.IsNullOrWhiteSpace(_snapshot.ErrorMessage) && _snapshot.Gemini5h is null
            ? LowBrush
            : BrushForStatus(worstGemini);

        var localTime = _snapshot.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        LastUpdatedText.Text = string.Format(
            CultureInfo.InvariantCulture,
            strings.StatusLastUpdatedFormat,
            localTime,
            _settings.PollIntervalMinutes);
    }

    private void ApplySettingsToControls(AppSettings settings)
    {
        _suppressSettingEvents = true;
        try
        {
            ApplyLanguage(settings.Language);
            SelectComboByTag(LanguageCombo, settings.Language.ToString());
            SelectComboByTag(PollIntervalCombo, settings.PollIntervalMinutes.ToString(CultureInfo.InvariantCulture));
            SelectComboByTag(TrayStyleCombo, settings.TrayIconStyle.ToString());
            OpenOnHoverCheck.IsChecked = settings.OpenOnHover;
            AutostartCheck.IsChecked = settings.AutostartEnabled;
            ShowClaudeCheck.IsChecked = settings.ShowClaudeOnChart;
            ClaudeLegendItem.Visibility = settings.ShowClaudeOnChart ? Visibility.Visible : Visibility.Collapsed;
            UpdateRangeButtons(settings.ChartRangeHours);
        }
        finally
        {
            _suppressSettingEvents = false;
        }
    }

    private void ApplyLanguage(AppLanguage language)
    {
        var s = AppStrings.For(language);
        HeaderBar.ToolTip = s.HeaderDragTooltip;
        RefreshButton.ToolTip = s.RefreshTooltip;
        PinButton.ToolTip = s.PinTooltip;
        SettingsButton.ToolTip = s.SettingsTooltip;

        TrayBadgeText.Text = s.TrayBadgeText;
        Gemini5hLabel.Text = s.Limit5hLabel;
        GeminiWeeklyLabel.Text = s.LimitWeeklyLabel;
        Claude5hLabel.Text = s.Limit5hLabel;
        ClaudeWeeklyLabel.Text = s.LimitWeeklyLabel;
        CreditsLabelText.Text = s.PrepaidCreditsLabel;
        HistoryTitleText.Text = s.HistoryTitle;
        LegendGeminiWeeklyText.Text = s.LegendGeminiWeekly;

        SettingsTitleText.Text = s.SettingsTitle;
        LanguageLabelText.Text = s.LanguageLabel;
        PollIntervalLabelText.Text = s.PollIntervalLabel;
        Poll1Item.Content = s.Poll1Min;
        Poll2Item.Content = s.Poll2Min;
        Poll5Item.Content = s.Poll5Min;
        Poll10Item.Content = s.Poll10Min;
        Poll15Item.Content = s.Poll15Min;
        Poll30Item.Content = s.Poll30Min;
        Poll60Item.Content = s.Poll60Min;

        TrayStyleLabelText.Text = s.TrayStyleLabel;
        StyleNumberItem.Content = s.StyleNumberWithWeeklyBar;
        StyleDualBarsItem.Content = s.StyleDualBars;
        StyleRingGaugeItem.Content = s.StyleRingGauge;

        OpenOnHoverCheck.Content = s.OpenOnHoverCheck;
        AutostartCheck.Content = s.AutostartCheck;
        OpenDataFolderBtn.Content = s.OpenDataFolderBtn;
        ExitAppBtn.Content = s.ExitAppBtn;
    }

    private void UpdateRangeButtons(int rangeHours)
    {
        SetRangeButtonState(Range6hBtn, rangeHours == 6);
        SetRangeButtonState(Range24hBtn, rangeHours == 24);
        SetRangeButtonState(Range7dBtn, rangeHours == 168);
    }

    private static void SetRangeButtonState(Button btn, bool active)
    {
        btn.Background = active ? ActiveRangeBg : InactiveRangeBg;
        btn.Foreground = active ? ActiveRangeFg : InactiveRangeFg;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }

    private static void ApplyBucket(
        QuotaBucket? bucket,
        TextBlock pctText,
        TextBlock resetText,
        Border barFill,
        DateTimeOffset nowUtc,
        AppLanguage language,
        StringBundle strings)
    {
        if (bucket is null)
        {
            pctText.Text = "--";
            pctText.Foreground = UnknownBrush;
            resetText.Text = "-";
            barFill.Width = 0;
            barFill.Background = UnknownBrush;
            return;
        }

        var brush = BrushForStatus(bucket.Status);
        pctText.Text = $"{bucket.RemainingPercent}%";
        pctText.Foreground = brush;
        resetText.Text = string.Format(
            CultureInfo.InvariantCulture,
            strings.ResetFormat,
            bucket.ResetLocalText,
            bucket.FormatResetIn(nowUtc, language));
        barFill.Background = brush;
        barFill.Width = Math.Max(0, ProgressTrackWidth * (Math.Clamp(bucket.RemainingPercent, 0, 100) / 100.0));
    }

    private static QuotaStatus GetWorstGeminiStatus(QuotaSnapshot snapshot)
    {
        var s5 = snapshot.Gemini5h?.Status ?? QuotaStatus.Unknown;
        var sw = snapshot.GeminiWeekly?.Status ?? QuotaStatus.Unknown;
        if (s5 == QuotaStatus.Low || sw == QuotaStatus.Low) return QuotaStatus.Low;
        if (s5 == QuotaStatus.Moderate || sw == QuotaStatus.Moderate) return QuotaStatus.Moderate;
        if (s5 == QuotaStatus.Ok || sw == QuotaStatus.Ok) return QuotaStatus.Ok;
        return QuotaStatus.Unknown;
    }

    private static SolidColorBrush BrushForStatus(QuotaStatus status) => status switch
    {
        QuotaStatus.Ok => OkBrush,
        QuotaStatus.Moderate => ModerateBrush,
        QuotaStatus.Low => LowBrush,
        _ => UnknownBrush
    };

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void OnPinClick(object sender, RoutedEventArgs e) => SetPinned(!IsPinned);

    private void OnSettingsToggleClick(object sender, RoutedEventArgs e)
    {
        var opening = SettingsPanel.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = opening
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateLayout();
        EnsureWithinWorkingArea();

        if (opening)
        {
            BodyScrollViewer.ScrollToBottom();
        }
    }

    private void OnRange6hClick(object sender, RoutedEventArgs e) => ChangeChartRange(6);
    private void OnRange24hClick(object sender, RoutedEventArgs e) => ChangeChartRange(24);
    private void OnRange7dClick(object sender, RoutedEventArgs e) => ChangeChartRange(168);

    private void ChangeChartRange(int hours)
    {
        _settings.ChartRangeHours = hours;
        UpdateRangeButtons(hours);
        SettingsChanged?.Invoke(_settings);
        ChartRangeChanged?.Invoke(hours);
    }

    private void OnShowClaudeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents) return;
        _settings.ShowClaudeOnChart = ShowClaudeCheck.IsChecked == true;
        ClaudeLegendItem.Visibility = _settings.ShowClaudeOnChart ? Visibility.Visible : Visibility.Collapsed;
        SettingsChanged?.Invoke(_settings);
        HistoryChart.SetData(_historyPoints, _settings.ChartRangeHours, _settings.ShowClaudeOnChart, _settings.Language);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents) return;
        if (LanguageCombo.SelectedItem is ComboBoxItem cbi &&
            Enum.TryParse<AppLanguage>(cbi.Tag?.ToString(), out var language))
        {
            _settings.Language = language;
            UpdateView(_snapshot, _historyPoints, _settings);
            SettingsChanged?.Invoke(_settings);
        }
    }

    private void OnPollIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents) return;
        if (PollIntervalCombo.SelectedItem is ComboBoxItem cbi &&
            int.TryParse(cbi.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            _settings.PollIntervalMinutes = minutes;
            UpdateHeaderStatus();
            SettingsChanged?.Invoke(_settings);
        }
    }

    private void OnTrayStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents) return;
        if (TrayStyleCombo.SelectedItem is ComboBoxItem cbi &&
            Enum.TryParse<TrayIconStyle>(cbi.Tag?.ToString(), out var style))
        {
            _settings.TrayIconStyle = style;
            SettingsChanged?.Invoke(_settings);
        }
    }

    private void OnOpenOnHoverChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents) return;
        _settings.OpenOnHover = OpenOnHoverCheck.IsChecked == true;
        SettingsChanged?.Invoke(_settings);
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents) return;
        _settings.AutostartEnabled = AutostartCheck.IsChecked == true;
        SettingsChanged?.Invoke(_settings);
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e) => OpenDataFolderRequested?.Invoke();

    private void OnExitAppClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
}
