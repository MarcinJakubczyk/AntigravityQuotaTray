using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntigravityQuotaTray.Models;

namespace AntigravityQuotaTray.Services;

public sealed class SettingsStore
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string DirectoryPath { get; }
    public string FilePath { get; }

    public SettingsStore(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntigravityQuotaTray");
        FilePath = Path.Combine(DirectoryPath, "settings.json");
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            try
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                if (settings.PollIntervalMinutes < 1)
                {
                    settings.PollIntervalMinutes = 1;
                }

                if (settings.ChartRangeHours is not (6 or 24 or 168))
                {
                    settings.ChartRangeHours = 24;
                }

                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tmpPath = FilePath + ".tmp";
            File.WriteAllText(tmpPath, json, Encoding.UTF8);
            File.Move(tmpPath, FilePath, overwrite: true);
        }
    }
}
