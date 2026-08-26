namespace XMEyeCloudTester;

/// <summary>
/// Protocol map extracted from the local VMS Pro binaries/PDBs, observed VMS
/// logs and the vendor FunSDK demo. It contains no account or camera data and
/// never performs a request by itself.
/// </summary>
internal static class DeviceConfigurationCatalog
{
    internal const int GenericReadCommand = 1042;
    internal const int GenericWriteCommand = 1040;

    internal enum AccessMode { ReadOnly, ReadWrite, Operation }
    internal enum RiskLevel { SafeRead, ControlledWrite, SensitiveWrite, Destructive }
    internal enum ChannelScope { Device, Channel, DeviceOrChannel }

    internal sealed record Definition(
        string Key, string Section, string Label, string JsonName,
        int? ReadCommand, int? WriteCommand, ChannelScope Scope,
        AccessMode Access, RiskLevel Risk, string RequiredCapability, string Evidence);

    internal static readonly Definition[] Definitions =
    [
        new("Identity.SystemInfo", "Sobre", "Informações do dispositivo", "SystemInfo", 1020, null, ChannelScope.Device, AccessMode.ReadOnly, RiskLevel.SafeRead, "", "VMS log + FunSDK"),
        new("Identity.SystemInfoEx", "Sobre", "Informações estendidas", "SystemInfoEx", 1020, null, ChannelScope.Device, AccessMode.ReadOnly, RiskLevel.SafeRead, "", "FunSDK"),
        new("Capability.SystemFunction", "Sobre", "Capacidades do firmware", "SystemFunction", 1360, null, ChannelScope.Device, AccessMode.ReadOnly, RiskLevel.SafeRead, "", "VMS log + FunSDK"),
        new("Basic.General", "Básicas", "Configurações gerais", "General.General", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS log + VMS PDB + FunSDK"),
        new("Basic.Location", "Básicas", "Local e idioma", "General.Location", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS log + FunSDK"),
        new("Time.TimeZone", "Básicas", "Fuso horário", "System.TimeZone", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS log + FunSDK"),
        new("Time.Current", "Básicas", "Data e hora", "OPTimeQuery", 1452, 1450, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS log"),
        new("Storage.Info", "Armazenamento", "Discos e cartão SD", "StorageInfo", 1020, null, ChannelScope.Device, AccessMode.ReadOnly, RiskLevel.SafeRead, "", "VMS PDB + FunSDK"),
        new("Storage.Snapshot", "Armazenamento", "Destino de fotos", "Storage.Snapshot", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS binary + FunSDK"),
        new("Storage.Manage", "Armazenamento", "Formatar/limpar armazenamento", "OPStorageManager", null, 1040, ChannelScope.Device, AccessMode.Operation, RiskLevel.Destructive, "", "FunSDK"),
        new("Recording.Main", "Gravação", "Gravação principal", "Record", 1042, 1040, ChannelScope.DeviceOrChannel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS PDB + FunSDK"),
        new("Recording.Extra", "Gravação", "Gravação de stream auxiliar", "ExtRecord", 1042, 1040, ChannelScope.DeviceOrChannel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "FunSDK"),
        new("Recording.Epitome", "Gravação", "Gravação resumida", "Storage.EpitomeRecord", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "FunSDK"),
        new("Alarm.Motion", "Alarme inteligente", "Detecção de movimento", "Detect.MotionDetect", 1042, 1040, ChannelScope.Channel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportMotionDetection", "VMS PDB + FunSDK"),
        new("Alarm.Human", "Alarme inteligente", "Detecção de pessoa", "Detect.HumanDetection", 1042, 1040, ChannelScope.Channel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportHumanDetection", "VMS log + FunSDK"),
        new("Alarm.Pir", "Alarme inteligente", "Sensor PIR", "Alarm.PIR", 1042, 1040, ChannelScope.Channel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "FunSDK"),
        new("Tracking.Motion", "Alarme inteligente", "Rastreamento de movimento", "Detect.DetectTrack", 1042, 1040, ChannelScope.DeviceOrChannel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportMotionTracking", "FunSDK"),
        new("Light.White", "Alarme sonoro e luminoso", "Luz branca/luz dupla", "Camera.WhiteLight", 1042, 1040, ChannelScope.DeviceOrChannel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportDoubleLightCamera", "VMS log + VMS PDB + FunSDK"),
        new("Alarm.IntelligentAlert", "Alarme sonoro e luminoso", "Vínculo inteligente", "Alarm.IntellAlertAlarm", 1042, 1040, ChannelScope.DeviceOrChannel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "FunSDK"),
        new("Alarm.VoiceType", "Alarme sonoro e luminoso", "Tipo de aviso sonoro", "Ability.VoiceTipType", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportAlarmSound", "FunSDK"),
        new("Audio.SpeakerVolume", "Áudio", "Volume do alto-falante", "fVideo.Volume", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportAlarmSound", "VMS log + FunSDK"),
        new("Audio.MicrophoneVolume", "Áudio", "Volume do microfone", "fVideo.VolumeIn", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportTwoWayTalk", "VMS log + FunSDK"),
        new("Camera.Parameters", "Avançadas", "Parâmetros de imagem", "Camera.Param", 1042, 1040, ChannelScope.Channel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS PDB + FunSDK"),
        new("Camera.ParametersEx", "Avançadas", "Parâmetros avançados de imagem", "Camera.ParamEx", 1042, 1040, ChannelScope.Channel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "", "VMS binary + FunSDK"),
        new("Ptz.Configuration", "Avançadas", "Orientação e configuração PTZ", "Uart.PTZControlCmd", 1042, 1040, ChannelScope.DeviceOrChannel, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportPTZDirectionControl", "VMS log + VMS PDB"),
        new("Ptz.Presets", "Avançadas", "Posições favoritas", "OPPTZControl", null, null, ChannelScope.Channel, AccessMode.Operation, RiskLevel.ControlledWrite, "SupportPtzPresets", "VMS PDB + FunSDK"),
        new("Network.Wifi", "Rede", "Wi-Fi", "NetWork.Wifi", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.SensitiveWrite, "SupportWifi", "VMS binary + FunSDK"),
        new("Network.Ntp", "Rede", "Servidor NTP", "NetWork.NetNTP", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.ControlledWrite, "SupportNtp", "VMS binary"),
        new("Network.Rtsp", "Rede", "RTSP", "NetWork.RTSP", 1042, 1040, ChannelScope.Device, AccessMode.ReadWrite, RiskLevel.SensitiveWrite, "SupportRtsp", "VMS binary"),
        new("Firmware.Upgrade", "Sobre", "Atualização de firmware", "OPFileUpgradeIPCReq", null, 2260, ChannelScope.Device, AccessMode.Operation, RiskLevel.Destructive, "SupportCloudUpgradeConfig", "VMS log")
    ];

    internal static Definition? Find(string key) =>
        Definitions.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
}
