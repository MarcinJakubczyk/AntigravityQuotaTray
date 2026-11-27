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

    private static readonly Color Gemini5hColor = Color.FromRgb(56, 189, 248);         // #38BDF8
    private static readonly Color GeminiIdleColor = Color.FromRgb(100, 116, 139);      // #64748B
    private static readonly Color UnattributedDropColor = Color.FromRgb(148, 163, 184); // #94A3B8
    private static readonly Color GeminiWeeklyColor = Color.FromRgb(52, 211, 153);     // #34D399
    private static readonly Color Claude5hColor = Color.FromRgb(245, 158, 11);         // #F59E0B

    private static readonly Pen Gemini5hPen = CreateFrozenPen(Gemini5hColor, 2.0);
    private static readonly Pen GeminiWeeklyPen = CreateFrozenPen(GeminiWeeklyColor, 1.7);
    private static readonly Pen Claude5hPen = CreateFrozenPen(Claude5hColor, 1.4, new DashStyle([4, 3], 0));
    private static readonly Brush Gemini5hAreaBrush = CreateAreaGradient(Gemini5hColor);

    private static readonly Pen VariantAIdlePen = CreateFrozenRoundPen(GeminiIdleColor, 1.8);
    private static readonly Pen VariantAWeeklyPen = CreateFrozenRoundPen(Color.FromArgb(210, GeminiWeeklyColor.R, GeminiWeeklyColor.G, GeminiWeeklyColor.B), 1.8);
    private static readonly Pen VariantAClaudePen = CreateFrozenPen(Claude5hColor, 1.8, new DashStyle([4, 3], 0));

    private static readonly SolidColorBrush TooltipBgBrush = CreateFrozenBrush(Color.FromArgb(240, 28, 28, 35));
    private static readonly Pen TooltipBorderPen = CreateFrozenPen(Color.FromRgb(75, 75, 90), 1.0);
    private static readonly SolidColorBrush TooltipTextBrush = CreateFrozenBrush(Color.FromRgb(244, 244, 245));
    private static readonly SolidColorBrush TooltipHeaderBrush = CreateFrozenBrush(Color.FromRgb(161, 161, 170));
    private static readonly Typeface UiTypeface = new("Segoe UI");

    private IReadOnlyList<HistoryPoint> _points = [];
    private IReadOnlyList<ModelUsageEvent> _modelEvents = [];
    private int _rangeHours = 24;
    private bool _showClaude;
    private AppLanguage _language = AppLanguage.En;
    private Point? _hoverPos;

    private readonly record struct SegmentStyle(bool IsDrop, Color PrimaryColor, Color? SecondaryColor);

    public bool ShowModelBreakdownOnChart { get; private set; }
    public event Action<bool>? ModelBreakdownToggled;

    public HistoryChartControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;
    }

    public void SetData(
        IReadOnlyList<HistoryPoint> points,
        int rangeHours,
        bool showClaude,
        AppLanguage language = AppLanguage.En,
        IReadOnlyList<ModelUsageEvent>? modelEvents = null)
    {
        _points = points;
        _rangeHours = Math.Max(1, rangeHours);
        _showClaude = showClaude;
        _language = language;
        _modelEvents = modelEvents ?? [];
        InvalidateVisual();
    }

    public void ToggleModelBreakdown()
    {
        ShowModelBreakdownOnChart = !ShowModelBreakdownOnChart;
        InvalidateVisual();
        ModelBreakdownToggled?.Invoke(ShowModelBreakdownOnChart);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        ToggleModelBreakdown();
        e.Handled = true;
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

        IReadOnlyList<DropAnnotation>? dropSpans = null;

        if (ShowModelBreakdownOnChart)
        {
            // Variant A: clean uniform 1.8px lines with model-colored drop slopes
            if (_showClaude)
            {
                DrawSeries(dc, visible, p => p.C5, MapTimeX, plot, VariantAClaudePen, null, Claude5hColor);
            }

            DrawSeries(dc, visible, p => p.Gw, MapTimeX, plot, VariantAWeeklyPen, null, GeminiWeeklyColor);

            dropSpans = ModelActivityTracker.DetectDropSpans(_points, p => p.G5, _modelEvents, minDropPercent: 1, maxPlateauMinutes: 6.0);
            DrawModelColoredGeminiSeries(dc, visible, dropSpans, MapTimeX, plot);
        }
        else
        {
            // Default: clean original chart appearance
            if (_showClaude)
            {
                DrawSeries(dc, visible, p => p.C5, MapTimeX, plot, Claude5hPen, null, Claude5hColor);
            }

            DrawSeries(dc, visible, p => p.Gw, MapTimeX, plot, GeminiWeeklyPen, null, GeminiWeeklyColor);
            DrawSeries(dc, visible, p => p.G5, MapTimeX, plot, Gemini5hPen, Gemini5hAreaBrush, Gemini5hColor);
        }

        if (visible.Count == 1)
        {
            var hint = CreateText(strings.ChartCollectingHint, 10, HintTextBrush, pixelsPerDip);
            dc.DrawText(hint, new Point(plot.Left + 8, plot.Top + 6));
        }

        if (_hoverPos is { } hp && plot.Contains(hp))
        {
            dropSpans ??= ModelActivityTracker.DetectDropSpans(_points, p => p.G5, _modelEvents, minDropPercent: 1, maxPlateauMinutes: 6.0);
            DrawHoverOverlay(dc, visible, dropSpans, hp, MapTimeX, plot, pixelsPerDip);
        }
    }

    private void DrawModelColoredGeminiSeries(
        DrawingContext dc,
        IReadOnlyList<HistoryPoint> visible,
        IReadOnlyList<DropAnnotation> dropSpans,
        Func<DateTimeOffset, double> mapX,
        Rect plot)
    {
        var g5Points = new List<(DateTimeOffset T, int Val, Point Coord)>(visible.Count);
        foreach (var p in visible)
        {
            if (p.G5.HasValue)
            {
                g5Points.Add((p.T, p.G5.Value, new Point(mapX(p.T), MapY(p.G5.Value, plot))));
            }
        }

        if (g5Points.Count == 0)
        {
            return;
        }

        if (g5Points.Count == 1)
        {
            dc.DrawEllipse(CreateFrozenBrush(Gemini5hColor), null, g5Points[0].Coord, 3.0, 3.0);
            return;
        }

        var segCount = g5Points.Count - 1;
        var styles = new SegmentStyle[segCount];

        for (var i = 0; i < segCount; i++)
        {
            var a = g5Points[i];
            var b = g5Points[i + 1];

            DropAnnotation? matchingSpan = null;
            if (b.Val <= a.Val)
            {
                foreach (var span in dropSpans)
                {
                    if ((a.T < span.EndUtc && b.T > span.StartUtc) ||
                        (b.Val < a.Val && span.StartUtc == span.EndUtc && a.T <= span.StartUtc && b.T >= span.EndUtc))
                    {
                        matchingSpan = span;
                        break;
                    }
                }
            }

            if (matchingSpan is null)
            {
                styles[i] = new SegmentStyle(false, GeminiIdleColor, null);
                continue;
            }

            IReadOnlyList<ModelShare> segModels;
            if ((matchingSpan.EndUtc - matchingSpan.StartUtc) <= TimeSpan.FromMinutes(25) && matchingSpan.Models.Count > 0)
            {
                segModels = matchingSpan.Models;
            }
            else
            {
                segModels = _modelEvents.Count > 0
                    ? ModelActivityTracker.SummarizeWindow(_modelEvents, a.T.AddMinutes(-5), b.T.AddMinutes(3))
                    : [];
                if (segModels.Count == 0)
                {
                    segModels = matchingSpan.Models;
                }
            }

            var c1 = segModels.Count > 0
                ? ToColor(ModelNameFormatter.GetFamilyRgb(segModels[0].FamilyKey))
                : UnattributedDropColor;

            Color? c2 = null;
            if (segModels.Count >= 2 && segModels[0].Share < 0.85)
            {
                for (var m = 1; m < segModels.Count; m++)
                {
                    var secModel = segModels[m];
                    if (secModel.Share >= 0.15 &&
                        !string.Equals(secModel.FamilyKey, segModels[0].FamilyKey, StringComparison.Ordinal))
                    {
                        var sec = ToColor(ModelNameFormatter.GetFamilyRgb(secModel.FamilyKey));
                        if (sec != c1)
                        {
                            c2 = sec;
                        }
                        break;
                    }
                }
            }

            styles[i] = new SegmentStyle(true, c1, c2);
        }

        var groups = new List<(SegmentStyle Style, List<Point> Coords)>();
        var curStyle = styles[0];
        var curCoords = new List<Point> { g5Points[0].Coord, g5Points[1].Coord };
        for (var i = 1; i < segCount; i++)
        {
            if (styles[i] == curStyle)
            {
                curCoords.Add(g5Points[i + 1].Coord);
            }
            else
            {
                groups.Add((curStyle, curCoords));
                curStyle = styles[i];
                curCoords = [g5Points[i].Coord, g5Points[i + 1].Coord];
            }
        }
        groups.Add((curStyle, curCoords));

        dc.PushClip(new RectangleGeometry(plot));
        try
        {
            foreach (var (style, coords) in groups)
            {
                if (!style.IsDrop || coords.Count < 2)
                {
                    continue;
                }

                var areaBrush = CreateSegmentGlowGradient(style.PrimaryColor);
                var areaGeo = new StreamGeometry();
                using (var ctx = areaGeo.Open())
                {
                    ctx.BeginFigure(new Point(coords[0].X, plot.Bottom), isFilled: true, isClosed: true);
                    for (var i = 0; i < coords.Count; i++)
                    {
                        ctx.LineTo(coords[i], isStroked: false, isSmoothJoin: false);
                    }

                    ctx.LineTo(new Point(coords[^1].X, plot.Bottom), isStroked: false, isSmoothJoin: false);
                }

                areaGeo.Freeze();
                dc.DrawGeometry(areaBrush, null, areaGeo);
            }

            foreach (var (style, coords) in groups)
            {
                if (style.IsDrop || coords.Count < 2)
                {
                    continue;
                }

                DrawPolyline(dc, coords, VariantAIdlePen);
            }

            foreach (var (style, coords) in groups)
            {
                if (!style.IsDrop || coords.Count < 2)
                {
                    continue;
                }

                if (style.SecondaryColor is { } secColor)
                {
                    var secPen = CreateFrozenRoundPen(secColor, 1.5);
                    DrawOffsetParallelSegments(dc, coords, offsetPx: 2.8, secPen);
                }

                var priPen = CreateFrozenRoundPen(style.PrimaryColor, 1.9);
                DrawPolyline(dc, coords, priPen);
            }
        }
        finally
        {
            dc.Pop();
        }

        var lastStyle = styles[^1];
        var endColor = lastStyle.IsDrop ? lastStyle.PrimaryColor : GeminiIdleColor;
        dc.DrawEllipse(CreateFrozenBrush(endColor), null, g5Points[^1].Coord, 3.0, 3.0);
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

            DrawPolyline(dc, coords, linePen);
        }

        var last = coords[^1];
        var dotBrush = CreateFrozenBrush(dotColor);
        dc.DrawEllipse(dotBrush, null, last, 3.0, 3.0);
    }

    private static void DrawPolyline(DrawingContext dc, List<Point> coords, Pen pen)
    {
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
        dc.DrawGeometry(null, pen, lineGeo);
    }

    private static void DrawOffsetParallelSegments(DrawingContext dc, List<Point> coords, double offsetPx, Pen pen)
    {
        for (var i = 1; i < coords.Count; i++)
        {
            var p0 = coords[i - 1];
            var p1 = coords[i];
            var dx = p1.X - p0.X;
            var dy = p1.Y - p0.Y;
            var len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len <= 0.01)
            {
                continue;
            }

            var nx = -dy / len;
            var ny = dx / len;
            if (ny < 0 || (Math.Abs(ny) < 0.05 && nx < 0))
            {
                nx = -nx;
                ny = -ny;
            }

            dc.DrawLine(
                pen,
                new Point(p0.X + (nx * offsetPx), p0.Y + (ny * offsetPx)),
                new Point(p1.X + (nx * offsetPx), p1.Y + (ny * offsetPx)));
        }
    }

    private void DrawHoverOverlay(
        DrawingContext dc,
        IReadOnlyList<HistoryPoint> points,
        IReadOnlyList<DropAnnotation> dropSpans,
        Point hoverPos,
        Func<DateTimeOffset, double> mapX,
        Rect plot,
        double pixelsPerDip)
    {
        HistoryPoint? nearest = null;
        var nearestIdx = -1;
        var bestDist = double.MaxValue;
        double nearestX = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var px = mapX(p.T);
            var dist = Math.Abs(px - hoverPos.X);
            if (dist < bestDist)
            {
                bestDist = dist;
                nearest = p;
                nearestIdx = i;
                nearestX = px;
            }
        }

        if (nearest is null)
        {
            return;
        }

        DropAnnotation? activeSpan = null;
        foreach (var span in dropSpans)
        {
            if (nearest.T >= span.StartUtc.AddMinutes(-1) && nearest.T <= span.EndUtc.AddMinutes(1))
            {
                activeSpan = span;
                break;
            }
        }

        dc.DrawLine(CrosshairPen, new Point(nearestX, plot.Top), new Point(nearestX, plot.Bottom));

        var g5DotColor = Gemini5hColor;
        if (ShowModelBreakdownOnChart && activeSpan is not null && activeSpan.Models.Count > 0)
        {
            g5DotColor = ToColor(ModelNameFormatter.GetFamilyRgb(activeSpan.Models[0].FamilyKey));
        }

        if (nearest.G5.HasValue)
        {
            dc.DrawEllipse(CreateFrozenBrush(g5DotColor), null, new Point(nearestX, MapY(nearest.G5.Value, plot)), 3.8, 3.8);
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
            ? localTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            : localTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

        var headerParts = new List<string> { timeLabel };
        if (nearest.G5.HasValue)
        {
            string deltaStr;
            if (activeSpan is not null)
            {
                deltaStr = $" (-{activeSpan.DropPercent}%)";
            }
            else
            {
                var prevG5 = FindPreviousValue(points, nearestIdx, p => p.G5);
                deltaStr = FormatDelta(nearest.G5.Value, prevG5);
            }

            headerParts.Add($"5h: {nearest.G5}%{deltaStr}");
        }

        if (nearest.Gw.HasValue)
        {
            var wkLabel = _language == AppLanguage.Pl ? "Tyg" : "Wk";
            headerParts.Add($"{wkLabel}: {nearest.Gw}%");
        }

        if (_showClaude && nearest.C5.HasValue)
        {
            headerParts.Add($"C5: {nearest.C5}%");
        }

        var headerText = CreateText(string.Join("  ·  ", headerParts), 9.5, TooltipHeaderBrush, pixelsPerDip);

        IReadOnlyList<ModelShare> shares;
        if (activeSpan is not null && activeSpan.Models.Count > 0)
        {
            shares = activeSpan.Models;
        }
        else
        {
            var prevPointTime = nearestIdx > 0 ? points[nearestIdx - 1].T : nearest.T;
            var minLookback = TimeSpan.FromMinutes(Math.Max(2, _rangeHours <= 6 ? 3 : _rangeHours <= 24 ? 5 : 15));
            var windowStart = (nearest.T - prevPointTime) > minLookback ? prevPointTime : nearest.T - minLookback;
            shares = ModelActivityTracker.SummarizeWindow(_modelEvents, windowStart, nearest.T.AddMinutes(1));
        }

        var displayShares = new List<ModelShare>(3);
        foreach (var share in shares)
        {
            if (share.Share >= 0.12 || displayShares.Count == 0)
            {
                displayShares.Add(share);
                if (displayShares.Count == 3)
                {
                    break;
                }
            }
        }

        var modelRows = new List<(Color DotColor, FormattedText Text)>(3);
        FormattedText? fallbackText = null;

        if (displayShares.Count > 0)
        {
            foreach (var share in displayShares)
            {
                var dotColor = ToColor(ModelNameFormatter.GetFamilyRgb(share.FamilyKey));
                var textStr = displayShares.Count == 1
                    ? share.DisplayName
                    : $"{share.DisplayName} ({(int)Math.Round(share.Share * 100.0, MidpointRounding.AwayFromZero)}%)";
                modelRows.Add((dotColor, CreateText(textStr, 9.5, TooltipTextBrush, pixelsPerDip)));
            }
        }
        else if (activeSpan is not null && !string.IsNullOrWhiteSpace(nearest.Models))
        {
            fallbackText = CreateText(nearest.Models, 9.5, TooltipTextBrush, pixelsPerDip);
        }

        const double padX = 7;
        const double padY = 5;
        const double dotColW = 10;
        const double rowGap = 2.0;

        var contentW = headerText.Width;
        var contentH = headerText.Height;

        foreach (var (_, rowText) in modelRows)
        {
            contentW = Math.Max(contentW, dotColW + rowText.Width);
            contentH += rowGap + rowText.Height;
        }

        if (fallbackText is not null)
        {
            contentW = Math.Max(contentW, fallbackText.Width);
            contentH += rowGap + fallbackText.Height;
        }

        var boxW = contentW + (padX * 2);
        var boxH = contentH + (padY * 2);

        var boxX = nearestX + 8;
        if (boxX + boxW > plot.Right)
        {
            boxX = Math.Max(plot.Left, nearestX - boxW - 8);
        }

        var boxY = Math.Min(plot.Top + 4, Math.Max(plot.Top, plot.Bottom - boxH));
        dc.DrawRoundedRectangle(TooltipBgBrush, TooltipBorderPen, new Rect(boxX, boxY, boxW, boxH), 5, 5);

        var curY = boxY + padY;
        dc.DrawText(headerText, new Point(boxX + padX, curY));
        curY += headerText.Height;

        foreach (var (dotColor, rowText) in modelRows)
        {
            curY += rowGap;
            var dotCenter = new Point(boxX + padX + 2.8, curY + (rowText.Height / 2.0));
            dc.DrawEllipse(CreateFrozenBrush(dotColor), null, dotCenter, 2.8, 2.8);
            dc.DrawText(rowText, new Point(boxX + padX + dotColW, curY));
            curY += rowText.Height;
        }

        if (fallbackText is not null)
        {
            curY += rowGap;
            dc.DrawText(fallbackText, new Point(boxX + padX, curY));
        }
    }

    private static int? FindPreviousValue(IReadOnlyList<HistoryPoint> points, int index, Func<HistoryPoint, int?> selector)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var v = selector(points[i]);
            if (v.HasValue)
            {
                return v.Value;
            }
        }

        return null;
    }

    private static string FormatDelta(int current, int? previous)
    {
        if (!previous.HasValue)
        {
            return string.Empty;
        }

        var diff = current - previous.Value;
        if (diff == 0)
        {
            return string.Empty;
        }

        return diff > 0 ? $" (+{diff}%)" : $" ({diff}%)";
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

    private static Color ToColor((byte R, byte G, byte B) rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);

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

    private static Pen CreateFrozenRoundPen(Color color, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
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

    private static Brush CreateSegmentGlowGradient(Color baseColor)
    {
        var brush = new LinearGradientBrush(
            Color.FromArgb(35, baseColor.R, baseColor.G, baseColor.B),
            Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B),
            new Point(0, 0),
            new Point(0, 1));
        brush.Freeze();
        return brush;
    }
}
