using System.IO;
using System.Text.Json;

namespace XMEyeCloudTester;

internal sealed class AppPreferences
{
    public int LastGridSize { get; set; } = 9;
    public bool RestoreLastLayout { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool DefaultSd { get; set; } = true;
    public int ConnectionTimeoutSeconds { get; set; } = 60;
    public int ReconnectDelaySeconds { get; set; } = 5;
    public List<string> LiveLayoutOrder { get; set; } = [];
    public string CaptureFolder { get; set; } = string.Empty;
    public string RecordingFolder { get; set; } = string.Empty;
    public bool StartWithWindows { get; set; }
    public int StorageLimitGb { get; set; } = 20;
    public string Language { get; set; } = "pt-BR";
    public string Theme { get; set; } = "Dark";

    public string GetCaptureFolder()
    {
        if (!string.IsNullOrWhiteSpace(CaptureFolder))
            return CaptureFolder;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "iCSee-XMEye");
    }

    public string GetRecordingFolder()
    {
        if (!string.IsNullOrWhiteSpace(RecordingFolder))
            return RecordingFolder;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "iCSee-XMEye");
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XMEyeCloudAccountTester",
        "settings.json");

    public static AppPreferences Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppPreferences();
            AppPreferences? loaded = JsonSerializer.Deserialize<AppPreferences>(
                File.ReadAllText(SettingsPath));
            if (loaded is null || loaded.LastGridSize is not (1 or 4 or 9 or 16))
                return new AppPreferences();
            if (loaded.ConnectionTimeoutSeconds is not (30 or 60 or 90))
                loaded.ConnectionTimeoutSeconds = 60;
            if (loaded.ReconnectDelaySeconds is not (3 or 5 or 10 or 15))
                loaded.ReconnectDelaySeconds = 5;
            return loaded;
        }
        catch
        {
            return new AppPreferences();
        }
    }

    public void Save()
    {
        string? directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
