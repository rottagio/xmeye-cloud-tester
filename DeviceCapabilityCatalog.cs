namespace XMEyeCloudTester;

/// <summary>
/// Canonical vocabulary for features reported by different XMEye firmware
/// generations. This table describes protocol fields, never individual cameras.
/// </summary>
internal static class DeviceCapabilityCatalog
{
    internal sealed record Definition(
        string Key,
        string Section,
        string Label,
        params string[] ProviderAliases);

    internal static readonly Definition[] Definitions =
    [
        new("SupportPTZDirectionControl", "Vídeo e PTZ", "Movimento PTZ",
            "SupportPTZDirectionControl"),
        new("SupportPtzPresets", "Vídeo e PTZ", "Posições favoritas",
            "SupportPtzPresets", "SupportSetPTZPresetAttribute"),
        new("SupportPtzTour", "Vídeo e PTZ", "Ronda PTZ",
            "SupportPtzTour", "SupportPTZTour"),
        new("SupportMotionTracking", "Vídeo e PTZ", "Rastreamento de movimento",
            "SupportMotionTracking", "SupportDetectTrack"),
        new("SupportTwoWayTalk", "Áudio", "Falar pela câmera",
            "SupportTwoWayTalk", "SupportTwoWayVoiceTalk", "Talk"),
        new("SupportMotionDetection", "Alarmes", "Detecção de movimento",
            "SupportMotionDetection", "MotionDetect"),
        new("SupportHumanDetection", "Alarmes", "Detecção de pessoa",
            "SupportHumanDetection", "SupportSmartAppHumanDetect",
            "HumanDection", "MotionHumanDection"),
        new("SupportAlarmSound", "Alarmes", "Aviso sonoro",
            "SupportAlarmSound", "SupportDVRAlarmSound", "SupportIPCAlarmSound",
            "SupportAlarmVoiceTips", "SupportAlarmVoiceTipsType"),
        new("SupportDoubleLightCamera", "Alarmes", "Luz branca / luz dupla",
            "SupportDoubleLightCamera", "SupportDoubleLightBoxCamera",
            "SupportPCSetDoubleLight", "SupportCameraWhiteLight",
            "SupportDoubleLightBulb"),
        new("SupportWifi", "Rede", "Wi-Fi", "SupportWifi", "NetWifi"),
        new("SupportRtsp", "Rede", "RTSP", "SupportRtsp", "NetRTSP"),
        new("SupportNtp", "Rede", "Sincronização NTP", "SupportNtp", "NetNTP"),
        new("SupportCloudUpgradeConfig", "Sistema", "Atualização pela nuvem",
            "SupportCloudUpgradeConfig", "SupportCfgCloudupgrade", "SupportCloudUpgrade")
    ];

    internal static Definition? Find(string key) =>
        Definitions.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
}
