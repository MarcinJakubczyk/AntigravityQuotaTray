using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AntigravityQuotaTray.Models;
using AntigravityQuotaTray.Services;

namespace AntigravityQuotaTray.UI;

public sealed class HistoryChartControl : FrameworkElement
{
    private static readonly SolidColorBrush BgBrush = CreateFrozenBrush(Color.FromRgb(18, 18, 22));
    private static readonly Pen BorderPen = CreateFrozenPen(Color.FromRgb(42, 42, 52), 1.0);
    private static readonly Pen GridPen = CreateFrozenPen(Color.FromRgb(34, 34, 44), 1.0);
    private static readonly Pen WarningPen = CreateFrozenPen(Color.FromArgb(140, 239, 68, 68), 1.0, new DashStyle([3, 3], 0));
    private static readonly Pen CrosshairPen = CreateFrozenPen(Color.FromArgb(120, 244, 244, 245), 1.0);
    private static readonly SolidColorBrush AxisTextBrush = CreateFrozenBrush(Color.FromRgb(113, 113, 122));
    private static readonly SolidColorBrush HintTextBrush = CreateFrozenBrush(Color.FromRgb(161, 161, 170));

    private static readonly Color Gemini5hColor = Color.FromRgb(56, 189, 248);
    private static readonly Color GeminiWeeklyColor = Color.FromRgb(52, 211, 153);
    private static readonly Color Claude5hColor = Color.FromRgb(245, 158, 11);

    private static readonly Pen Gemini5hPen = CreateFrozenPen(Gemini5hColor, 2.0);
    private static readonly Pen GeminiWeeklyPen = CreateFrozenPen(GeminiWeeklyColor, 1.7);
    private static readonly Pen Claude5hPen = CreateFrozenPen(Claude5hColor, 1.4, new DashStyle([4, 3], 0));
    private static readonly Brush Gemini5hAreaBrush = CreateAreaGradient(Gemini5hColor);

    private static readonly SolidColorBrush TooltipBgBrush = CreateFrozenBrush(Color.FromArgb(240, 28, 28, 35));
    private static readonly Pen TooltipBorderPen = CreateFrozenPen(Color.FromRgb(75, 75, 90), 1.0);
    private static readonly SolidColorBrush TooltipTextBrush = CreateFrozenBrush(Color.FromRgb(244, 244, 245));
    private static readonly Typeface UiTypeface = new("Segoe UI");

    private IReadOnlyList<HistoryPoint> _points = [];
    private int _rangeHours = 24;
    private bool _showClaude;
    private AppLanguage _language = AppLanguage.En;
    private Point? _hoverPos;

    public HistoryChartControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public void SetData(IReadOnlyList<HistoryPoint> points, int rangeHours, bool showClaude, AppLanguage language = AppLanguage.En)
    {
        _points = points;
        _rangeHours = Math.Max(1, rangeHours);
        _showClaude = showClaude;
        _language = language;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverPos = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPos = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 60 || height < 50)
        {
            return;
        }

        var fullRect = new Rect(0.5, 0.5, width - 1, height - 1);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, fullRect, 8, 8);

        const double padLeft = 30;
        const double padTop = 10;
        const double padRight = 10;
        const double padBottom = 20;

        var plot = new Rect(
            padLeft,
            padTop,
            Math.Max(10, width - padLeft - padRight),
            Math.Max(10, height - padTop - padBottom));

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Horizontal grid & Y labels
        foreach (var pct in new[] { 0, 25, 50, 75, 100 })
        {
            var y = MapY(pct, plot);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));

            if (pct is 0 or 50 or 100)
            {
                var label = CreateText($"{pct}%", 9.5, AxisTextBrush, pixelsPerDip);
                dc.DrawText(label, new Point(4, y - (label.Height / 2.0)));
            }
        }

        // 20% warning threshold
        var warnY = MapY(20, plot);
        dc.DrawLine(WarningPen, new Point(plot.Left, warnY), new Point(plot.Right, warnY));

        var endUtc = DateTimeOffset.UtcNow;
        var startUtc = endUtc - TimeSpan.FromHours(_rangeHours);
        var spanSeconds = Math.Max(1.0, (endUtc - startUtc).TotalSeconds);

        // X-axis time ticks
        var tickCount = 3;
        for (var i = 0; i <= tickCount; i++)
        {
            var ratio = i / (double)tickCount;
            var tickTime = startUtc.AddSeconds(spanSeconds * ratio).ToLocalTime();
            var tickText = _rangeHours <= 24
                ? tickTime.ToString("HH:mm", CultureInfo.InvariantCulture)
                : tickTime.ToString("dd.MM", CultureInfo.InvariantCulture);

            var formatted = CreateText(tickText, 9.5, AxisTextBrush, pixelsPerDip);
            var x = plot.Left + (plot.Width * ratio);
            var textX = i == 0 ? plot.Left : (i == tickCount ? plot.Right - formatted.Width : x - (formatted.Width / 2.0));
            dc.DrawText(formatted, new Point(textX, plot.Bottom + 3));
        }

        var strings = AppStrings.For(_language);
        var visible = Downsample(_points, 220);
        if (visible.Count == 0)
        {
            var emptyText = CreateText(strings.ChartEmptyHint, 10.5, HintTextBrush, pixelsPerDip);
            dc.DrawText(emptyText, new Point(
                plot.Left + Math.Max(0, (plot.Width - emptyText.Width) / 2.0),
                plot.Top + Math.Max(0, (plot.Height - emptyText.Height) / 2.0)));
            return;
        }

        double MapTimeX(DateTimeOffset t)
        {
            var rel = (t.ToUniversalTime() - startUtc).TotalSeconds / spanSeconds;
            return plot.Left + (Math.Clamp(rel, 0.0, 1.0) * plot.Width);
        }

        if (_showClaude)
        {
            DrawSeries(dc, visible, p => p.C5, MapTimeX, plot, Claude5hPen, null, Claude5hColor);
        }

        DrawSeries(dc, visible, p => p.Gw, MapTimeX, plot, GeminiWeeklyPen, null, GeminiWeeklyColor);
        DrawSeries(dc, visible, p => p.G5, MapTimeX, plot, Gemini5hPen, Gemini5hAreaBrush, Gemini5hColor);

        if (visible.Count == 1)
        {
            var hint = CreateText(strings.ChartCollectingHint, 10, HintTextBrush, pixelsPerDip);
            dc.DrawText(hint, new Point(plot.Left + 8, plot.Top + 6));
        }

        if (_hoverPos is { } hp && plot.Contains(hp))
        {
            DrawHoverOverlay(dc, visible, hp, MapTimeX, plot, pixelsPerDip);
        }
    }

    private static void DrawSeries(
        DrawingContext dc,
        IReadOnlyList<HistoryPoint> points,
        Func<HistoryPoint, int?> selector,
        Func<DateTimeOffset, double> mapX,
        Rect plot,
        Pen linePen,
        Brush? areaBrush,
        Color dotColor)
    {
        var coords = new List<Point>(points.Count);
        foreach (var p in points)
        {
            var val = selector(p);
            if (val.HasValue)
            {
                coords.Add(new Point(mapX(p.T), MapY(val.Value, plot)));
            }
        }

        if (coords.Count == 0)
        {
            return;
        }

        if (coords.Count >= 2)
        {
            if (areaBrush is not null)
            {
                var areaGeo = new StreamGeometry();
                using (var ctx = areaGeo.Open())
                {
                    ctx.BeginFigure(new Point(coords[0].X, plot.Bottom), isFilled: true, isClosed: true);
                    ctx.LineTo(coords[0], isStroked: false, isSmoothJoin: false);
                    for (var i = 1; i < coords.Count; i++)
                    {
                        ctx.LineTo(coords[i], isStroked: false, isSmoothJoin: false);
                    }

                    ctx.LineTo(new Point(coords[^1].X, plot.Bottom), isStroked: false, isSmoothJoin: false);
                }

                areaGeo.Freeze();
                dc.DrawGeometry(areaBrush, null, areaGeo);
            }

            var lineGeo = new StreamGeometry();
            using (var ctx = lineGeo.Open())
            {
                ctx.BeginFigure(coords[0], isFilled: false, isClosed: false);
                for (var i = 1; i < coords.Count; i++)
                {
                    ctx.LineTo(coords[i], isStroked: true, isSmoothJoin: true);
                }
            }

            lineGeo.Freeze();
            dc.DrawGeometry(null, linePen, lineGeo);
        }

        var last = coords[^1];
        var dotBrush = CreateFrozenBrush(dotColor);
        dc.DrawEllipse(dotBrush, null, last, 3.0, 3.0);
    }

    private void DrawHoverOverlay(
        DrawingContext dc,
        IReadOnlyList<HistoryPoint> points,
        Point hoverPos,
        Func<DateTimeOffset, double> mapX,
        Rect plot,
        double pixelsPerDip)
    {
        HistoryPoint? nearest = null;
        var bestDist = double.MaxValue;
        double nearestX = 0;

        foreach (var p in points)
        {
            var px = mapX(p.T);
            var dist = Math.Abs(px - hoverPos.X);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = p;
                nearestX = px;
            }
        }

        if (nearest is null)
        {
            return;
        }

        dc.DrawLine(CrosshairPen, new Point(nearestX, plot.Top), new Point(nearestX, plot.Bottom));

        if (nearest.G5.HasValue)
        {
            dc.DrawEllipse(CreateFrozenBrush(Gemini5hColor), null, new Point(nearestX, MapY(nearest.G5.Value, plot)), 3.8, 3.8);
        }

        if (nearest.Gw.HasValue)
        {
            dc.DrawEllipse(CreateFrozenBrush(GeminiWeeklyColor), null, new Point(nearestX, MapY(nearest.Gw.Value, plot)), 3.5, 3.5);
        }

        if (_showClaude && nearest.C5.HasValue)
        {
            dc.DrawEllipse(CreateFrozenBrush(Claude5hColor), null, new Point(nearestX, MapY(nearest.C5.Value, plot)), 3.5, 3.5);
        }

        var localTime = nearest.T.ToLocalTime();
        var timeLabel = _rangeHours <= 24
            ? localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : localTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

        var lines = new List<string> { timeLabel };
        if (nearest.G5.HasValue) lines.Add($"Gemini 5h: {nearest.G5}%");
        if (nearest.Gw.HasValue) lines.Add($"{AppStrings.For(_language).ChartTooltipGeminiWeekly}: {nearest.Gw}%");
        if (_showClaude && nearest.C5.HasValue) lines.Add($"Claude 5h: {nearest.C5}%");

        var tooltipText = CreateText(string.Join("\n", lines), 10, TooltipTextBrush, pixelsPerDip);
        const double padX = 7;
        const double padY = 5;
        var boxW = tooltipText.Width + (padX * 2);
        var boxH = tooltipText.Height + (padY * 2);

        var boxX = nearestX + 8;
        if (boxX + boxW > plot.Right)
        {
            boxX = Math.Max(plot.Left, nearestX - boxW - 8);
        }

        var boxY = plot.Top + 4;
        dc.DrawRoundedRectangle(TooltipBgBrush, TooltipBorderPen, new Rect(boxX, boxY, boxW, boxH), 5, 5);
        dc.DrawText(tooltipText, new Point(boxX + padX, boxY + padY));
    }

    private static IReadOnlyList<HistoryPoint> Downsample(IReadOnlyList<HistoryPoint> points, int maxCount)
    {
        if (points.Count <= maxCount)
        {
            return points;
        }

        var sampled = new List<HistoryPoint>(maxCount);
        var step = (points.Count - 1) / (double)(maxCount - 1);
        for (var i = 0; i < maxCount; i++)
        {
            var idx = (int)Math.Round(i * step);
            sampled.Add(points[Math.Clamp(idx, 0, points.Count - 1)]);
        }

        return sampled;
    }

    private static double MapY(int percent, Rect plot)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        return plot.Bottom - (plot.Height * (clamped / 100.0));
    }

    private static FormattedText CreateText(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiTypeface, size, brush, pixelsPerDip);

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }

    private static Pen CreateFrozenPen(Color color, double thickness, DashStyle? dash = null)
    {
        var pen = new Pen(CreateFrozenBrush(color), thickness);
        if (dash is not null)
        {
            pen.DashStyle = dash;
        }

        pen.Freeze();
        return pen;
    }

    private static Brush CreateAreaGradient(Color baseColor)
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(65, baseColor.R, baseColor.G, baseColor.B),
            Color.FromArgb(4, baseColor.R, baseColor.G, baseColor.B),
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
