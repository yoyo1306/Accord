using System.Text;
using System.Text.Json;
using Accord.Core.Models;

namespace Accord.Core.Storage;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppSettings Load()
    {
        try
        {
            string path = AppPaths.SettingsPath;
            if (!File.Exists(path)) return new AppSettings();
            string json = File.ReadAllText(path, Encoding.UTF8);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null) return new AppSettings();
            loaded.Profiles ??= new ProfileTargets();
            loaded.PollIntervalSeconds = Math.Clamp(loaded.PollIntervalSeconds, 1, 15);
            return loaded;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        string path = AppPaths.SettingsPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static WatcherStatus? LoadWatcherStatus()
    {
        try
        {
            string path = AppPaths.WatcherStatusPath;
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<WatcherStatus>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveWatcherStatus(WatcherStatus status)
    {
        try
        {
            string path = AppPaths.WatcherStatusPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(status, JsonOptions);
            string tmp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Non-fatal status broadcast failure.
        }
    }
}
