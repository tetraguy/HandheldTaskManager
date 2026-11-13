using System.IO;
using System.Text.Json;

namespace HandheldTaskManager.Services;

public enum AppTheme { Auto, Dark, Light }
public enum SortKey { Memory, Cpu, Name }

/// <summary>User settings, stored in %AppData%\HandheldTaskManager\settings.json.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.Auto;
    public SortKey SortBy { get; set; } = SortKey.Memory;
    public bool SortDescending { get; set; } = true;
    /// <summary>Milliseconds between updates; 0 means paused.</summary>
    public int RefreshMs { get; set; } = 2000;
    public bool AlwaysOnTop { get; set; }
    public bool StartFullscreen { get; set; }
    public int LastTab { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HandheldTaskManager", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt or unreadable settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
