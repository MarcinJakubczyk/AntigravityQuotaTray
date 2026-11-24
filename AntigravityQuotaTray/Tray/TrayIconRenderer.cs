using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using AntigravityQuotaTray.Models;
using AntigravityQuotaTray.Services;

namespace AntigravityQuotaTray.Tray;

public static class TrayIconRenderer
{
    private const int IconSize = 32;

    public static Icon RenderGeminiIcon(QuotaSnapshot? snapshot, TrayIconStyle style)
    {
        using var bitmap = new Bitmap(IconSize, IconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        DrawBackdrop(g);

        switch (style)
        {
            case TrayIconStyle.DualBars:
                DrawDualBars(g, snapshot?.Gemini5h, snapshot?.GeminiWeekly);
                break;
            case TrayIconStyle.RingGauge:
                DrawRingGauge(g, snapshot?.Gemini5h, snapshot?.GeminiWeekly);
                break;
            case TrayIconStyle.NumberWithWeeklyBar:
            default:
                DrawNumberWithWeeklyBar(g, snapshot?.Gemini5h, snapshot?.GeminiWeekly);
                break;
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    public static string BuildTooltipText(QuotaSnapshot? snapshot, AppLanguage language = AppLanguage.En)
    {
        var strings = AppStrings.For(language);
        string raw;
        if (snapshot is null)
        {
            raw = strings.TooltipLoading;
        }
        else if (snapshot.Gemini5h is null && snapshot.GeminiWeekly is null)
        {
            raw = string.IsNullOrWhiteSpace(snapshot.ErrorMessage)
                ? strings.TooltipNoData
                : strings.TooltipReadError;
        }
        else
        {
            var p5 = snapshot.Gemini5h is not null ? $"{snapshot.Gemini5h.RemainingPercent}%" : "--";
            var pw = snapshot.GeminiWeekly is not null ? $"{snapshot.GeminiWeekly.RemainingPercent}%" : "--";
            raw = string.Format(CultureInfo.InvariantCulture, strings.TooltipSummaryFormat, p5, pw);
        }

        return raw.Length <= 63 ? raw : raw[..63];
    }

    private static void DrawBackdrop(Graphics g)
    {
        using var path = CreateRoundedRect(new RectangleF(0.25f, 0.25f, IconSize - 0.5f, IconSize - 0.5f), 5f);
        using var bgBrush = new SolidBrush(Color.FromArgb(255, 24, 24, 27));
        using var borderPen = new Pen(Color.FromArgb(220, 63, 63, 70), 1f);
        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);
    }

    private static void DrawNumberWithWeeklyBar(Graphics g, QuotaBucket? g5, QuotaBucket? gw)
    {
        var topColor = GetStatusColor(g5?.Status ?? QuotaStatus.Unknown);
        var text = g5 is not null ? g5.RemainingPercent.ToString(CultureInfo.InvariantCulture) : "--";
        var fontSize = text.Length >= 3 ? 12.5f : 16.5f;

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(topColor);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };

        var textRect = new RectangleF(0f, 0.5f, IconSize, 22f);
        g.DrawString(text, font, textBrush, textRect, format);

        var barRect = new RectangleF(2.5f, 23.5f, IconSize - 5f, 5.5f);
        DrawHorizontalBar(g, barRect, gw?.RemainingPercent ?? 0, gw?.Status ?? QuotaStatus.Unknown);
    }

    private static void DrawDualBars(Graphics g, QuotaBucket? g5, QuotaBucket? gw)
    {
        var topRect = new RectangleF(2.5f, 5.5f, IconSize - 5f, 8.5f);
        var bottomRect = new RectangleF(2.5f, 18f, IconSize - 5f, 8.5f);

        DrawHorizontalBar(g, topRect, g5?.RemainingPercent ?? 0, g5?.Status ?? QuotaStatus.Unknown);
        DrawHorizontalBar(g, bottomRect, gw?.RemainingPercent ?? 0, gw?.Status ?? QuotaStatus.Unknown);
    }

    private static void DrawRingGauge(Graphics g, QuotaBucket? g5, QuotaBucket? gw)
    {
        var c5 = GetStatusColor(g5?.Status ?? QuotaStatus.Unknown);
        var cw = GetStatusColor(gw?.Status ?? QuotaStatus.Unknown);

        var outerRect = new RectangleF(3.5f, 3.5f, IconSize - 7f, IconSize - 7f);
        using var trackPen = new Pen(Color.FromArgb(255, 45, 45, 54), 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(trackPen, outerRect, 0, 360);

        if (g5 is not null && g5.RemainingPercent > 0)
        {
            var sweep = 360f * (g5.RemainingPercent / 100f);
            using var arcPen = new Pen(c5, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arcPen, outerRect, -90f, sweep);
        }

        var innerRect = new RectangleF(9.5f, 9.5f, IconSize - 19f, IconSize - 19f);
        using var innerTrackPen = new Pen(Color.FromArgb(255, 39, 39, 46), 2.5f);
        g.DrawArc(innerTrackPen, innerRect, 0, 360);

        if (gw is not null && gw.RemainingPercent > 0)
        {
            var sweepW = 360f * (gw.RemainingPercent / 100f);
            using var innerArcPen = new Pen(cw, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(innerArcPen, innerRect, -90f, sweepW);
        }
    }

    private static void DrawHorizontalBar(Graphics g, RectangleF rect, int percent, QuotaStatus status)
    {
        var radius = Math.Min(2.5f, rect.Height / 2f);
        using var trackPath = CreateRoundedRect(rect, radius);
        using var trackBrush = new SolidBrush(Color.FromArgb(255, 39, 39, 46));
        g.FillPath(trackBrush, trackPath);

        if (status == QuotaStatus.Unknown || percent <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(percent, 0, 100);
        var fillWidth = Math.Max(rect.Height, rect.Width * (clamped / 100f));
        var fillRect = new RectangleF(rect.X, rect.Y, Math.Min(rect.Width, fillWidth), rect.Height);

        using var fillPath = CreateRoundedRect(fillRect, radius);
        using var fillBrush = new SolidBrush(GetStatusColor(status));
        g.FillPath(fillBrush, fillPath);
    }

    private static Color GetStatusColor(QuotaStatus status) => status switch
    {
        QuotaStatus.Ok => Color.FromArgb(255, 34, 197, 94),
        QuotaStatus.Moderate => Color.FromArgb(255, 245, 158, 11),
        QuotaStatus.Low => Color.FromArgb(255, 239, 68, 68),
        _ => Color.FromArgb(255, 148, 163, 184)
    };

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2f;
        if (diameter <= 0 || diameter > bounds.Width || diameter > bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
