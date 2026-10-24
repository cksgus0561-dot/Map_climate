using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TheGarden.Desktop;

/// <summary>Light "atlas" and dark "night atlas" palettes, swapped in the application resources at run time.
/// Map colours (terrain, climate layers) are not themed: the same world reads the same in both.</summary>
public static class ThemeManager
{
    public static bool Dark { get; private set; }
    private static readonly Dictionary<string, string> Light = new()
    {
        ["Paper"] = "#EFE9DD", ["Card"] = "#FBF8F2", ["CardSoft"] = "#F4EFE5", ["Ink"] = "#2A2824", ["Muted"] = "#7B7466", ["Rule"] = "#DDD4C3",
        ["Teal"] = "#2E6B66", ["TealSoft"] = "#DCE9E5", ["Ochre"] = "#B8682C", ["OchreSoft"] = "#F3E2D1", ["Alert"] = "#A4462F",
        ["HoverFill"] = "#ECE4D6", ["PressFill"] = "#E2D8C6", ["MenuHover"] = "#EFE7D9", ["LayerHover"] = "#F1EADD", ["OnInk"] = "#FBF8F2",
        ["ActionText"] = "#FFFCF6", ["ActionHover"] = "#A35A24", ["FieldBg"] = "#FFFDF9", ["SelectionFill"] = "#9CC2BB", ["TrackFill"] = "#EADFCD", ["ThumbFill"] = "#CFC5B3",
    };
    private static readonly Dictionary<string, string> Night = new()
    {
        ["Paper"] = "#15171B", ["Card"] = "#1E2127", ["CardSoft"] = "#262A31", ["Ink"] = "#E8E2D6", ["Muted"] = "#9C9588", ["Rule"] = "#343841",
        ["Teal"] = "#74BDB2", ["TealSoft"] = "#203735", ["Ochre"] = "#DE9154", ["OchreSoft"] = "#3B2B1F", ["Alert"] = "#E8907A",
        ["HoverFill"] = "#2B2F37", ["PressFill"] = "#353A43", ["MenuHover"] = "#2B2F37", ["LayerHover"] = "#272B32", ["OnInk"] = "#16181C",
        ["ActionText"] = "#1A130D", ["ActionHover"] = "#EBA56E", ["FieldBg"] = "#16191E", ["SelectionFill"] = "#3D6A63", ["TrackFill"] = "#30343C", ["ThumbFill"] = "#4A505A",
    };

    public static void Apply(bool dark)
    {
        var resources = Application.Current.Resources;
        foreach (var (key, hex) in dark ? Night : Light)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); resources[key] = brush;
        }
        Dark = dark;
    }

    /// <summary>Windows "app mode" (Settings → Personalisation → Colours); light when unknown.</summary>
    public static bool SystemPrefersDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch (Exception) { return false; }
    }
}
