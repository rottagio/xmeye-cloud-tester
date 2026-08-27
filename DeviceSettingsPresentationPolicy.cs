namespace XMEyeCloudTester;

/// <summary>
/// Keeps the per-camera settings screen honest: a feature is shown as usable
/// only after evidence from that specific device/firmware.
/// </summary>
internal static class DeviceSettingsPresentationPolicy
{
    internal static readonly string[] CustomerSections =
    [
        "Básicas", "Armazenamento", "Gravação", "Alarme inteligente",
        "Som e luz", "Áudio", "Rede", "Avançadas", "Sobre"
    ];

    internal static bool ShowConfirmed(DeviceProfileStore.ConfigurationBinding? binding) =>
        binding?.Supported == true;

    internal static bool OfferSafeProbe(string configurationKey) =>
        configurationKey is "Storage.Info" or "Recording.Main";

    /// <summary>
    /// Keeps protocol/diagnostic features out of the customer settings menu.
    /// RTSP, firmware operations and PTZ presets remain available in their
    /// dedicated product areas instead of appearing as camera adjustments.
    /// </summary>
    internal static bool IsCustomerFacingConfiguration(string configurationKey) =>
        configurationKey is
            "Basic.General" or "Time.TimeZone" or "Time.Current" or
            "Storage.Info" or "Recording.Main" or "Recording.Extra" or
            "Alarm.Motion" or "Alarm.Human" or "Alarm.Pir" or
            "Tracking.Motion" or "Light.White" or "Alarm.IntelligentAlert" or
            "Alarm.VoiceType" or "Audio.SpeakerVolume" or
            "Audio.MicrophoneVolume" or "Network.Wifi" or
            "Camera.Parameters" or "Identity.SystemInfo";
}
