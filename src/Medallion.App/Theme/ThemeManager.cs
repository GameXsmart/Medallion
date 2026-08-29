using System.Windows;
using System.Windows.Media;
using Medallion.Core.Config;
using Medallion.Core.Diagnostics;

namespace Medallion.App.Theme;

/// <summary>
/// Applies a colour palette at runtime.
///
/// The palette keys are referenced throughout the XAML as DynamicResource, so replacing an
/// entry in Application.Resources repaints every control that uses it immediately — no
/// window reload, no flicker, and it works as many times as the user cares to switch.
/// </summary>
public static class ThemeManager
{
    private sealed record Palette(
        string Bg0, string Bg1, string Bg2, string Bg3,
        string Stroke, string StrokeSoft, string StrokeStrong,
        string Text, string TextDim, string TextFaint);

    /// <summary>Deep grey surfaces — easier on the eyes on an LCD.</summary>
    private static readonly Palette Dark = new(
        Bg0: "#FF0A0B0F", Bg1: "#FF101218", Bg2: "#FF161923", Bg3: "#FF1D212C",
        Stroke: "#FF232733", StrokeSoft: "#FF1A1E28", StrokeStrong: "#FF343A4A",
        Text: "#FFE9EBF2", TextDim: "#FF9AA0B4", TextFaint: "#FF6B7186");

    /// <summary>
    /// True black. On an OLED panel a #000000 pixel is simply off, so this both looks
    /// deeper and draws less power. Surfaces stay black and are separated by borders
    /// instead of by lighter fills.
    /// </summary>
    private static readonly Palette Amoled = new(
        Bg0: "#FF000000", Bg1: "#FF000000", Bg2: "#FF0A0A0C", Bg3: "#FF131318",
        Stroke: "#FF1F1F27", StrokeSoft: "#FF121217", StrokeStrong: "#FF2E2E3A",
        Text: "#FFFFFFFF", TextDim: "#FFA2A2B2", TextFaint: "#FF70707E");

    public static void Apply(AppTheme theme)
    {
        var palette = theme == AppTheme.Amoled ? Amoled : Dark;
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        Set(resources, "Bg0", palette.Bg0);
        Set(resources, "Bg1", palette.Bg1);
        Set(resources, "Bg2", palette.Bg2);
        Set(resources, "Bg3", palette.Bg3);
        Set(resources, "Stroke", palette.Stroke);
        Set(resources, "StrokeSoft", palette.StrokeSoft);
        Set(resources, "StrokeStrong", palette.StrokeStrong);
        Set(resources, "Text", palette.Text);
        Set(resources, "TextDim", palette.TextDim);
        Set(resources, "TextFaint", palette.TextFaint);

        Log.Info($"Theme applied: {theme}");
    }

    /// <summary>Background for the notification window, which lives outside the main tree.</summary>
    public static Brush ToastBackground(AppTheme theme) =>
        new SolidColorBrush(theme == AppTheme.Amoled
            ? Color.FromArgb(0xF7, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xF2, 0x14, 0x17, 0x20));

    public static Brush ToastBorder(AppTheme theme) =>
        new SolidColorBrush(theme == AppTheme.Amoled
            ? Color.FromRgb(0x24, 0x24, 0x2E)
            : Color.FromRgb(0x2A, 0x2F, 0x3D));

    /// <summary>
    /// Replaces a palette entry at the application level.
    ///
    /// WPF freezes brushes declared in a ResourceDictionary, so their colour cannot be
    /// edited in place. Instead the entry is overwritten with a new brush: every reference
    /// is a DynamicResource, so the change propagates through the live visual tree
    /// immediately. Writing into Application.Resources also shadows the merged theme
    /// dictionary, which is what makes repeated switching work.
    /// </summary>
    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze(); // immutable and cheap to share; it is replaced, never edited

            resources[key] = brush;
        }
        catch (Exception ex)
        {
            Log.Debug($"Theme key '{key}' could not be applied: {ex.Message}");
        }
    }
}
