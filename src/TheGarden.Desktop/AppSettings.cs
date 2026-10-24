using System.IO;
using System.Text.Json;

namespace TheGarden.Desktop;

/// <summary>Per-user app preferences (%APPDATA%/TheGarden/settings.json); missing or broken files give defaults.</summary>
public sealed class AppSettings
{
    public double TextScale { get; set; } = 1;
    public bool? Dark { get; set; }            // null: follow the Windows app mode
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheGarden", "settings.json");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static void Save(AppSettings settings)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(settings)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
