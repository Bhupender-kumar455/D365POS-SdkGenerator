using System.Text.Json;
using D365SdkGenerator.Models;

namespace D365SdkGenerator.Services;

public sealed class SettingsService
{
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<GeneratorSettings> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        var settings = await JsonSerializer.DeserializeAsync<GeneratorSettings>(stream, options, cancellationToken);
        return settings ?? throw new InvalidOperationException($"Unable to load settings from '{filePath}'.");
    }

    /// <summary>
    /// The catalog is published beside the executable. The current directory is checked as
    /// a fallback so the app also works when launched from a development build folder.
    /// </summary>
    public static string LocateCatalogFile()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "sdk-catalog.json"),
            Path.Combine(Environment.CurrentDirectory, "sdk-catalog.json")
        };

        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            "sdk-catalog.json was not found. Place it next to the executable.");
    }
}
