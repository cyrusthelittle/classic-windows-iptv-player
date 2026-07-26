using System.Collections.Generic;
using System.Windows.Media;
using WpfApplication = System.Windows.Application;

namespace ClassicWindowsIptvPlayer.Windows;

// Swaps the color/brush resources declared in App.xaml between the light and
// dark palettes. All XAML that should follow the theme references these keys
// with DynamicResource, so replacing the resources re-themes open windows.
internal static class ThemeManager
{
    public static bool IsDark { get; private set; }

    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["Bg0"] = ("#FFFFFF", "#0F172A"),
        ["Bg1"] = ("#FFFFFF", "#0F172A"),
        ["Bg2"] = ("#F8FAFC", "#16213A"),
        ["Panel"] = ("#FFFFFF", "#1E293B"),
        ["Stroke"] = ("#CBD5E1", "#334155"),
        ["Accent"] = ("#DBEAFE", "#2E4066"),
        ["Accent2"] = ("#7C3AED", "#8B5CF6"),
        ["Text0"] = ("#000000", "#F1F5F9"),
        ["Text1"] = ("#000000", "#E2E8F0"),
        ["Text2"] = ("#111827", "#94A3B8"),
        ["InputBg"] = ("#FFFFFF", "#111C33"),
        ["ButtonBg"] = ("#FFFFFF", "#243350"),
        ["OverlayScrim"] = ("#F2F8FAFC", "#F20B1220"),
        ["InputLightBg"] = ("#F8FAFC", "#111C33"),
        ["InputLightText"] = ("#0F172A", "#F1F5F9"),
        ["InputLightBorder"] = ("#64748B", "#475569")
    };

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var resources = WpfApplication.Current.Resources;
        foreach (var (key, values) in Palette)
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? values.Dark : values.Light);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = color;
            resources[key + "Brush"] = brush;
        }
    }
}
