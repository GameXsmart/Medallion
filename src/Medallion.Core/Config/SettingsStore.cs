using System.Text.Json;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Config;

/// <summary>
/// Loads and saves <see cref="Settings"/> as JSON under %APPDATA%\Replay.
/// Saves are atomic (write-temp + replace) so a crash mid-write cannot corrupt the file,
/// and a corrupt file is quarantined rather than blocking startup.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    public string Directory { get; }
    public string FilePath { get; }

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Medallion");
        FilePath = Path.Combine(Directory, "settings.json");
    }

    public Settings Load()
    {
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (!File.Exists(FilePath))
                {
                    var fresh = new Settings();
                    fresh.Normalize();
                    SaveCore(fresh);
                    return fresh;
                }

                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
                loaded.Normalize();
                return loaded;
            }
            catch (Exception ex)
            {
                Log.Error("Settings could not be read; falling back to defaults", ex);
                try
                {
                    if (File.Exists(FilePath))
                        File.Move(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                }
                catch { /* best effort */ }

                var fallback = new Settings();
                fallback.Normalize();
                return fallback;
            }
        }
    }

    public void Save(Settings settings)
    {
        lock (_gate)
        {
            try
            {
                settings.Normalize();
                SaveCore(settings);
            }
            catch (Exception ex)
            {
                Log.Error("Settings could not be saved", ex);
            }
        }
    }

    private void SaveCore(Settings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));

        if (File.Exists(FilePath))
            File.Replace(tmp, FilePath, null);
        else
            File.Move(tmp, FilePath);
    }
}
