using System.Text.Json;
using DwreanTv.Models;

namespace DwreanTv.Services;

public sealed class SettingsService
{
    private readonly string _settingsPath;

    public SettingsService()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Portable settings should never prevent the player from running.
        }
    }

    public static string GetChannelKey(Channel channel)
    {
        return string.IsNullOrWhiteSpace(channel.EpgId)
            ? channel.Name
            : channel.EpgId;
    }
}

public sealed class AppSettings
{
    public HashSet<string> FavoriteKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastChannelKey { get; set; } = string.Empty;
    public int Volume { get; set; } = 80;
}
