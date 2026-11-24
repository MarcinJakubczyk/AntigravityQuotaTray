using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AntigravityQuotaTray.UI;

public partial class ResetProgressPill : UserControl
{
    private static readonly SolidColorBrush ActiveTextBrush = CreateFrozen(Color.FromRgb(228, 228, 231));
    private static readonly SolidColorBrush MutedTextBrush = CreateFrozen(Color.FromRgb(161, 161, 170));

    public ResetProgressPill()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Updates the pill label, rising progress fill [0..1], and tooltip.
    /// </summary>
    public void SetState(string text, double? progressFraction, string? tooltip)
    {
        LabelText.Text = text;
        ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip;

        if (progressFraction is double raw)
        {
            var p = Math.Clamp(raw, 0.0, 1.0);
            FillColumn.Width = new GridLength(p, GridUnitType.Star);
            RestColumn.Width = new GridLength(Math.Max(0.0, 1.0 - p), GridUnitType.Star);
            ProgressFill.Visibility = p > 0.001 ? Visibility.Visible : Visibility.Collapsed;
            ProgressFill.BorderThickness = (p > 0.01 && p < 0.99)
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(0);
            LabelText.Foreground = ActiveTextBrush;
        }
        else
        {
            FillColumn.Width = new GridLength(0, GridUnitType.Star);
            RestColumn.Width = new GridLength(1, GridUnitType.Star);
            ProgressFill.Visibility = Visibility.Collapsed;
            ProgressFill.BorderThickness = new Thickness(0);
            LabelText.Foreground = MutedTextBrush;
        }
    }

    private static SolidColorBrush CreateFrozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
