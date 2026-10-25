using System.IO;
using System.Text.Json;

namespace D365SdkGenerator.App;

/// <summary>
/// Remembers the last used identity and location so repeat runs need no typing. Stored
/// under %LOCALAPPDATA%; a corrupt or unwritable file is never allowed to block startup.
/// </summary>
internal sealed class AppPreferences
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string? ProjectName { get; set; }
    public string? Publisher { get; set; }
    public string? OutputDirectory { get; set; }
    public string? SdkVersion { get; set; }
    public bool InitializeGit { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "D365SdkGenerator",
        "app-settings.json");

    public static AppPreferences Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppPreferences>(json, Options);

                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Preferences are a convenience; fall back to defaults.
        }

        return new AppPreferences();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Preferences are a convenience; ignore IO failures.
        }
    }
}
