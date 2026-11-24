using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Threading;
using AntigravityQuotaTray.Models;
using AntigravityQuotaTray.Services;
using AntigravityQuotaTray.UI;

namespace AntigravityQuotaTray.Tray;

public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly PopupWindow _popup;
    private readonly ToolStripMenuItem _refreshMenuItem;
    private readonly ToolStripMenuItem _toggleMenuItem;
    private readonly ToolStripMenuItem _autostartMenuItem;
    private readonly ToolStripMenuItem _openFolderMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;
    private readonly DispatcherTimer _hoverOpenTimer;
    private readonly DispatcherTimer _hoverWatchTimer;

    private AppSettings _settings;
    private Icon? _currentIcon;
    private NativeMethods.POINT _trayAnchorPx;
    private DateTime _lastInsideHoverAreaUtc = DateTime.UtcNow;
    private bool _disposed;

    public event Action? RefreshRequested;
    public event Action<bool>? AutostartToggledFromMenu;
    public event Action? OpenDataFolderRequested;
    public event Action? ExitRequested;

    public TrayManager(PopupWindow popup, AppSettings initialSettings)
    {
        _popup = popup;
        _settings = initialSettings;

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ForeColor = System.Drawing.Color.FromArgb(244, 244, 245),
            BackColor = System.Drawing.Color.FromArgb(24, 24, 27),
            ShowImageMargin = false
        };

        _refreshMenuItem = new ToolStripMenuItem();
        _refreshMenuItem.Click += (_, _) => RefreshRequested?.Invoke();

        _toggleMenuItem = new ToolStripMenuItem();
        _toggleMenuItem.Click += (_, _) => TogglePopupFromClick();

        _autostartMenuItem = new ToolStripMenuItem
        {
            Checked = _settings.AutostartEnabled,
            CheckOnClick = true
        };
        _autostartMenuItem.Click += (_, _) => AutostartToggledFromMenu?.Invoke(_autostartMenuItem.Checked);

        _openFolderMenuItem = new ToolStripMenuItem();
        _openFolderMenuItem.Click += (_, _) => OpenDataFolderRequested?.Invoke();

        _exitMenuItem = new ToolStripMenuItem();
        _exitMenuItem.Click += (_, _) => ExitRequested?.Invoke();

        ApplyLanguage(_settings.Language);

        menu.Items.Add(_refreshMenuItem);
        menu.Items.Add(_toggleMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostartMenuItem);
        menu.Items.Add(_openFolderMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitMenuItem);

        _currentIcon = TrayIconRenderer.RenderGeminiIcon(null, _settings.TrayIconStyle);

        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = TrayIconRenderer.BuildTooltipText(null, _settings.Language),
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.MouseMove += OnNotifyIconMouseMove;
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;

        _hoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _hoverOpenTimer.Tick += OnHoverOpenTick;

        _hoverWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _hoverWatchTimer.Tick += OnHoverWatchTick;
    }

    public void UpdateDisplay(QuotaSnapshot? snapshot, AppSettings settings)
    {
        _settings = settings;
        _autostartMenuItem.Checked = settings.AutostartEnabled;
        ApplyLanguage(settings.Language);

        var newIcon = TrayIconRenderer.RenderGeminiIcon(snapshot, settings.TrayIconStyle);
        var oldIcon = _currentIcon;
        _currentIcon = newIcon;
        _notifyIcon.Icon = newIcon;
        _notifyIcon.Text = TrayIconRenderer.BuildTooltipText(snapshot, settings.Language);
        oldIcon?.Dispose();
    }

    private void ApplyLanguage(AppLanguage language)
    {
        var s = AppStrings.For(language);
        _refreshMenuItem.Text = s.MenuRefreshNow;
        _toggleMenuItem.Text = s.MenuToggleWindow;
        _autostartMenuItem.Text = s.MenuAutostart;
        _openFolderMenuItem.Text = s.MenuOpenDataFolder;
        _exitMenuItem.Text = s.MenuExit;
    }

    private void OnNotifyIconMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_settings.OpenOnHover)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt))
        {
            return;
        }

        _lastInsideHoverAreaUtc = DateTime.UtcNow;

        if (!_popup.IsVisible)
        {
            _trayAnchorPx = pt;
            if (!_hoverOpenTimer.IsEnabled)
            {
                _hoverOpenTimer.Start();
            }
        }
        else if (DistanceSq(pt, _trayAnchorPx) <= 32 * 32)
        {
            _trayAnchorPx = pt;
        }
    }

    private void OnHoverOpenTick(object? sender, EventArgs e)
    {
        _hoverOpenTimer.Stop();

        if (!_settings.OpenOnHover || _popup.IsVisible)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt))
        {
            return;
        }

        if (DistanceSq(pt, _trayAnchorPx) > 40 * 40)
        {
            return;
        }

        _lastInsideHoverAreaUtc = DateTime.UtcNow;
        ShowPopupAtAnchor(_trayAnchorPx, pinned: false, activate: false);
        _hoverWatchTimer.Start();
    }

    private void OnHoverWatchTick(object? sender, EventArgs e)
    {
        if (!_popup.IsVisible)
        {
            _hoverWatchTimer.Stop();
            return;
        }

        if (_popup.IsPinned || _popup.IsSettingsOpen)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var pt))
        {
            return;
        }

        var insideTrayIcon = Math.Abs(pt.X - _trayAnchorPx.X) <= 28 && Math.Abs(pt.Y - _trayAnchorPx.Y) <= 28;
        var insidePopup = IsPointInsidePopup(pt);

        if (insideTrayIcon || insidePopup)
        {
            _lastInsideHoverAreaUtc = DateTime.UtcNow;
            return;
        }

        if ((DateTime.UtcNow - _lastInsideHoverAreaUtc).TotalMilliseconds >= 350)
        {
            _hoverWatchTimer.Stop();
            _popup.Hide();
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _hoverOpenTimer.Stop();

        if (NativeMethods.GetCursorPos(out var pt))
        {
            _trayAnchorPx = pt;
        }

        TogglePopupFromClick();
    }

    private void TogglePopupFromClick()
    {
        if (_popup.IsVisible && _popup.IsPinned)
        {
            _popup.SetPinned(false);
            _hoverWatchTimer.Stop();
            _popup.Hide();
            return;
        }

        if (_trayAnchorPx.X == 0 && _trayAnchorPx.Y == 0 && NativeMethods.GetCursorPos(out var pt))
        {
            _trayAnchorPx = pt;
        }

        ShowPopupAtAnchor(_trayAnchorPx, pinned: true, activate: true);
    }

    private void ShowPopupAtAnchor(NativeMethods.POINT anchorPx, bool pinned, bool activate)
    {
        _popup.SetPinned(pinned);

        if (!_popup.IsVisible)
        {
            // Ensure layout size is measured before computing position
            _popup.Left = -10000;
            _popup.Top = -10000;
            _popup.Show();
        }

        _popup.UpdateLayout();

        var screen = Screen.FromPoint(new System.Drawing.Point(anchorPx.X, anchorPx.Y));
        var workArea = screen.WorkingArea;

        var dpi = VisualTreeHelper.GetDpi(_popup);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        var anchorDipX = anchorPx.X / scaleX;
        var anchorDipY = anchorPx.Y / scaleY;
        var workLeft = workArea.Left / scaleX;
        var workTop = workArea.Top / scaleY;
        var workRight = workArea.Right / scaleX;
        var workBottom = workArea.Bottom / scaleY;

        _popup.UpdateMaxHeightForWorkArea(workBottom - workTop);
        _popup.UpdateLayout();

        var width = _popup.ActualWidth > 0 ? _popup.ActualWidth : _popup.Width;
        var height = _popup.ActualHeight > 0 ? _popup.ActualHeight : 480;
        const double margin = 10.0;

        var left = anchorDipX - (width / 2.0);
        left = Math.Max(workLeft + margin, Math.Min(left, workRight - width - margin));

        double top;
        if (anchorDipY >= workBottom - 16)
        {
            // Taskbar at bottom
            top = workBottom - height - margin;
        }
        else if (anchorDipY <= workTop + 16)
        {
            // Taskbar at top
            top = workTop + margin;
        }
        else
        {
            top = Math.Max(workTop + margin, Math.Min(anchorDipY - height - 16, workBottom - height - margin));
        }

        _popup.Left = left;
        _popup.Top = top;

        if (activate)
        {
            _popup.Activate();
        }
    }

    private bool IsPointInsidePopup(NativeMethods.POINT pt)
    {
        if (!_popup.IsVisible)
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(_popup);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        var x = pt.X / scaleX;
        var y = pt.Y / scaleY;

        var left = _popup.Left - 8;
        var top = _popup.Top - 8;
        var right = _popup.Left + _popup.ActualWidth + 8;
        var bottom = _popup.Top + _popup.ActualHeight + 24; // bridge gap down to taskbar

        return x >= left && x <= right && y >= top && y <= bottom;
    }

    private static long DistanceSq(NativeMethods.POINT a, NativeMethods.POINT b)
    {
        long dx = a.X - b.X;
        long dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hoverOpenTimer.Stop();
        _hoverWatchTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = System.Drawing.Color.FromArgb(244, 244, 245);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(24, 24, 27);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(46, 46, 56);
        private static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(63, 63, 70);

        public override System.Drawing.Color MenuItemSelected => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override System.Drawing.Color MenuItemBorder => Border;
        public override System.Drawing.Color MenuBorder => Border;
        public override System.Drawing.Color ToolStripDropDownBackground => Bg;
        public override System.Drawing.Color ImageMarginGradientBegin => Bg;
        public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
        public override System.Drawing.Color ImageMarginGradientEnd => Bg;
        public override System.Drawing.Color SeparatorDark => Border;
        public override System.Drawing.Color SeparatorLight => Border;
    }
}
