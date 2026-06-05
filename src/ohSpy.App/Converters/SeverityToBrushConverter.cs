namespace ohSpy.App.Converters;

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using ohSpy.Core.Diagnostics;
using Windows.UI;

/// <summary>
/// Maps a <see cref="DiagSeverity"/> to a foreground/accent <see cref="Brush"/> for the Diagnostics
/// viewer's severity cell (AC-5.1.2): Warning → amber, Error → red, Information → neutral (default
/// foreground), Verbose → muted. App-side because Pattern 2 forbids <see cref="Brush"/> in Core.
/// <para>
/// Referenced as a <c>{StaticResource}</c> inside the row <c>DataTemplate</c> — the template root is a
/// <see cref="FrameworkElement"/>, so the converter-lookup-root is available (unlike the Window-root
/// projections PropertiesWindow/SubscriptionPopupWindow had to push to code-behind).
/// </para>
/// </summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    // Fixed palette (v1 — story permits). Amber/red chosen for high-contrast legibility on the Mica
    // backdrop; Information/Verbose fall back to theme-driven brushes for neutral/muted text.
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23)); // red
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(0xFF, 0xF7, 0x63, 0x0C)); // amber/orange

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var severity = value is DiagSeverity s ? s : DiagSeverity.Information;
        return severity switch
        {
            DiagSeverity.Error => ErrorBrush,
            DiagSeverity.Warning => WarningBrush,
            DiagSeverity.Verbose => MutedBrush(),
            _ => DefaultForegroundBrush(), // Information (neutral)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    // Theme-resolved muted/neutral brushes (fall back to a fixed grey/white if the resource is absent,
    // e.g. design-time), so Information/Verbose track the current theme.
    private static Brush MutedBrush() =>
        Application.Current.Resources.TryGetValue("MutedForegroundBrush", out var b) && b is Brush brush
            ? brush
            : new SolidColorBrush(Color.FromArgb(0xFF, 0x76, 0x76, 0x76));

    private static Brush DefaultForegroundBrush() =>
        Application.Current.Resources.TryGetValue("TextFillColorPrimaryBrush", out var b) && b is Brush brush
            ? brush
            : new SolidColorBrush(Colors.White);
}
