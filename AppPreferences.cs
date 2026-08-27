using System.IO;
using System.Text.Json;

namespace XMEyeCloudTester;

internal sealed class AppPreferences
{
    public int LastGridSize { get; set; } = 9;
    // O VMS Pro usa autoRealPlay=false. Novas instalacoes aguardam uma acao
    // do usuario; perfis que optaram por restaurar esperam o monitor estabilizar.
    public bool RestoreLastLayout { get; set; } = false;
    // A recuperação permanece limitada pelas proteções do SDK e pelo intervalo
    // configurado; novas instalações devem recuperar canais isolados sem exigir
    // intervenção manual.
    public bool AutoReconnect { get; set; } = true;
    public int RecoveryPolicyVersion { get; set; } = 1;
    public bool DefaultSd { get; set; } = true;
    public int ConnectionTimeoutSeconds { get; set; } = 60;
    public int ReconnectDelaySeconds { get; set; } = 60;
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
            string json = File.ReadAllText(SettingsPath);
            using JsonDocument document = JsonDocument.Parse(json);
            bool hasRecoveryPolicyVersion = document.RootElement.TryGetProperty(
                nameof(RecoveryPolicyVersion), out _);
            AppPreferences? loaded = JsonSerializer.Deserialize<AppPreferences>(json);
            if (loaded is null || loaded.LastGridSize is not (1 or 4 or 9 or 16))
                return new AppPreferences();
            if (ApplyRecoveryPolicyMigration(loaded, hasRecoveryPolicyVersion))
            {
                loaded.Save();
            }
            if (loaded.ConnectionTimeoutSeconds is not (30 or 60 or 90))
                loaded.ConnectionTimeoutSeconds = 60;
            if (loaded.ReconnectDelaySeconds is not (60 or 120 or 300 or 900))
                loaded.ReconnectDelaySeconds = 60;
            return loaded;
        }
        catch
        {
            return new AppPreferences();
        }
    }

    internal static bool ApplyRecoveryPolicyMigration(
        AppPreferences preferences, bool hasPersistedVersion)
    {
        if (hasPersistedVersion)
            return false;
        // Versões anteriores podiam persistir AutoReconnect=false por causa do
        // antigo modo de diagnóstico controlado. A migração é executada uma
        // única vez; escolhas posteriores são respeitadas.
        preferences.AutoReconnect = true;
        preferences.RecoveryPolicyVersion = 1;
        return true;
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
