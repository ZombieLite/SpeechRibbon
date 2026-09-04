using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace SpeechRibbon;

internal static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static void Apply(ResourceDictionary resources)
    {
        var isLight = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            isLight = Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) != 0;
        }
        catch
        {
            // The Windows default is the safe fallback when theme data is unavailable.
        }

        var accent = SystemParameters.WindowGlassColor;
        accent = accent.A == 0 ? Color.FromRgb(50, 107, 255) : Color.FromRgb(accent.R, accent.G, accent.B);
        var accentText = RelativeLuminance(accent) > 0.48 ? Colors.Black : Colors.White;

        Set(resources, "PageBrush", isLight ? "#F4F7FA" : "#181A1F");
        Set(resources, "CardBrush", isLight ? "#FFFFFF" : "#24272E");
        Set(resources, "FieldBrush", isLight ? "#F8FAFC" : "#1E2127");
        Set(resources, "TableHeaderBrush", isLight ? "#F8FAFC" : "#2D313A");
        Set(resources, "InkBrush", isLight ? "#142238" : "#F2F4F7");
        Set(resources, "MutedBrush", isLight ? "#64748B" : "#AAB2C0");
        Set(resources, "BorderBrush", isLight ? "#CBD5E1" : "#4B5563");
        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentTextBrush"] = new SolidColorBrush(accentText);
        Set(resources, "CurrentStatusBrush", isLight ? "#DCFCE7" : "#143C2A");
        Set(resources, "CurrentStatusTextBrush", isLight ? "#166534" : "#86EFAC");
        Set(resources, "WindowButtonHoverBrush", isLight ? "#E8EDF3" : "#343840");
        Set(resources, "ScrollThumbBrush", isLight ? "#94A3B8" : "#64748B");
        Set(resources, "ScrollThumbHoverBrush", isLight ? "#64748B" : "#94A3B8");
    }

    private static void Set(ResourceDictionary resources, string key, string color) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var v = value / 255d;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
