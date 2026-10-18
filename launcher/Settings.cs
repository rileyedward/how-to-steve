using System.Text.Json;

namespace SteveFish.Launcher;

/// <summary>%LOCALAPPDATA%\SteveFish\launcher.json</summary>
internal sealed class Settings
{
    public string? GameDir { get; set; }
    public bool StopWithGame { get; set; } = true;
    public bool LaunchGame { get; set; } = true;
    public int ServerMemoryMb { get; set; } = 2048;

    private static string FilePath => Path.Combine(Detect.DataDir, "launcher.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception) { /* corrupt: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Detect.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
