using XMEyeCloudTester;

var catalog = new CameraCatalogStore();
catalog.Cameras["legacy"] = new CameraCatalogStore.Entry
{
    DetectedChannelCount = 1,
    SecondaryChannelProbeFailures = 0
};
catalog.Cameras["confirmed-single"] = new CameraCatalogStore.Entry
{
    DetectedChannelCount = 1,
    SecondaryChannelProbeFailures = 2,
    LastSecondaryChannelProbeFailureUtc = DateTime.UtcNow.AddDays(-1)
};
catalog.Cameras["confirmed-double"] = new CameraCatalogStore.Entry
{
    DetectedChannelCount = 2,
    KnownChannels = [0, 1]
};

int migrated = catalog.MigrateLegacyChannelDetections();
Require(migrated == 1, $"Esperava migrar 1 registro; migrou {migrated}.");
Require(catalog.Cameras["legacy"].DetectedChannelCount is null,
    "O registro legado não voltou ao estado desconhecido.");
Require(catalog.Cameras["confirmed-single"].DetectedChannelCount == 1,
    "Uma câmera de um canal validada foi alterada.");
Require(catalog.Cameras["confirmed-double"].DetectedChannelCount == 2 &&
        catalog.Cameras["confirmed-double"].SecondaryChannelConfirmedEver,
    "Uma câmera de dois canais confirmada perdeu sua evidência.");

Console.WriteLine("CHANNEL_MIGRATION_REGRESSION_OK");

Require(ConnectionRecoveryPolicy.ErrorCooldown(-27, 1) == TimeSpan.FromHours(1),
    "O bloqueio -27 não preservou a quarentena de uma hora.");
Require(ConnectionRecoveryPolicy.ErrorCooldown(-25, 1) == TimeSpan.FromMinutes(10),
    "O limite -25 não recebeu a pausa de dez minutos.");
Require(ConnectionRecoveryPolicy.ErrorCooldown(-8, 1) == TimeSpan.FromMinutes(10),
    "O erro -8 não preservou o mínimo de dez minutos.");
Require(ConnectionRecoveryPolicy.ErrorCooldown(-7, 3) == TimeSpan.FromMinutes(4),
    "O recuo exponencial comum foi alterado.");
Require(ConnectionRecoveryPolicy.PassiveGrace(unstable: true) == TimeSpan.FromSeconds(15),
    "O modo instável não recebeu a janela passiva ampliada.");
Require(ConnectionRecoveryPolicy.PassiveCycleMinimum(true, 60) == TimeSpan.FromMinutes(5),
    "O modo instável não limitou os ciclos passivos.");
Require(ConnectionRecoveryPolicy.DeviceLoginMinimum(true, 60) == TimeSpan.FromMinutes(5),
    "O modo instável não limitou o login único do dispositivo.");
Require(ConnectionRecoveryPolicy.DeviceLoginMinimum(false, 30) == TimeSpan.FromMinutes(1),
    "O login normal não preservou o intervalo mínimo de um minuto.");
Console.WriteLine("CONNECTION_RECOVERY_POLICY_OK");

var profiles = new DeviceProfileStore();
profiles.RecordCapability("camera-a", "SupportWifi", false, "SystemFunction: SupportWifi");
profiles.RecordCapability("camera-a", "SupportPTZDirectionControl", true,
    "SystemFunction: SupportPTZDirectionControl");
Require(profiles.GetCapability("camera-a", "SupportWifi").State ==
        DeviceProfileStore.CapabilityState.Unavailable,
    "Uma resposta negativa da câmera foi confundida com recurso desconhecido.");
Require(profiles.GetCapability("camera-a", "SupportPTZDirectionControl", "PTZ.Direction").State ==
        DeviceProfileStore.CapabilityState.Available,
    "A evidência positiva agregada de PTZ não foi preservada.");
Require(profiles.GetCapability("camera-a", "SupportNtp").State ==
        DeviceProfileStore.CapabilityState.Unknown,
    "Um recurso não informado foi marcado como incompatível.");
Require(DeviceCapabilityCatalog.Definitions.SelectMany(item => item.ProviderAliases)
        .Contains("HumanDection", StringComparer.Ordinal),
    "O catálogo perdeu a grafia legada usada por firmwares XMEye.");
Console.WriteLine("DEVICE_CAPABILITY_PROFILE_OK");

Require(DeviceConfigurationCatalog.Definitions.Select(item => item.Key)
        .Distinct(StringComparer.Ordinal).Count() == DeviceConfigurationCatalog.Definitions.Length,
    "O catálogo de configurações contém chaves duplicadas.");
Require(DeviceConfigurationCatalog.Definitions
        .Where(item => item.RequiredCapability.Length > 0)
        .All(item => DeviceCapabilityCatalog.Find(item.RequiredCapability) is not null),
    "Uma configuração depende de uma capacidade que não existe no catálogo.");
Require(DeviceConfigurationCatalog.Definitions
        .Where(item => item.Risk == DeviceConfigurationCatalog.RiskLevel.Destructive)
        .All(item => item.Access == DeviceConfigurationCatalog.AccessMode.Operation),
    "Uma ação destrutiva foi catalogada como configuração comum.");
Require(DeviceConfigurationCatalog.Definitions
        .Where(item => item.WriteCommand is not null)
        .All(item => item.Risk != DeviceConfigurationCatalog.RiskLevel.SafeRead),
    "Um comando de gravação foi classificado como leitura segura.");

profiles.RebuildConfigurationBindings("camera-a");
DeviceProfileStore.ConfigurationBinding human =
    profiles.Devices["camera-a"].CompatibleCommands["Alarm.Human"];
Require(human.Supported is null && human.JsonName == "Detect.HumanDetection" &&
        human.ReadCommand == 1042 && human.WriteCommand == 1040,
    "O perfil não preservou o comando de detecção humana ainda desconhecido.");
profiles.RecordCapability("camera-a", "SupportHumanDetection", true,
    "SystemFunction: HumanDection");
human = profiles.Devices["camera-a"].CompatibleCommands["Alarm.Human"];
Require(human.Supported == true && human.Evidence == "SystemFunction: HumanDection",
    "A capacidade confirmada não habilitou o comando correspondente.");
DeviceProfileStore.ConfigurationBinding wifi =
    profiles.Devices["camera-a"].CompatibleCommands["Network.Wifi"];
Require(wifi.Supported == false && wifi.Risk == "SensitiveWrite",
    "O Wi-Fi incompatível ou seu risco não foi preservado no perfil.");
profiles.RecordConfigurationEvidence("camera-a", "Storage.Info", true,
    "StorageInfo: resposta válida");
Require(profiles.Devices["camera-a"].CompatibleCommands["Storage.Info"].Supported == true,
    "A evidência direta de um comando não foi registrada.");
Console.WriteLine("DEVICE_CONFIGURATION_CATALOG_OK");

DeviceConfigurationCatalog.Definition systemFunction =
    DeviceConfigurationCatalog.Find("Capability.SystemFunction")!;
DeviceConfigurationCatalog.Definition storageManage =
    DeviceConfigurationCatalog.Find("Storage.Manage")!;
DeviceConfigurationCatalog.Definition wifiConfig =
    DeviceConfigurationCatalog.Find("Network.Wifi")!;
DeviceConfigurationCatalog.Definition whiteLight =
    DeviceConfigurationCatalog.Find("Light.White")!;
Require(DeviceConfigurationReadPolicy.CanDiscoverAutomatically(systemFunction),
    "SystemFunction deixou de ser a única descoberta automática permitida.");
Require(!DeviceConfigurationReadPolicy.CanDiscoverAutomatically(wifiConfig),
    "Uma leitura de rede sensível foi liberada automaticamente.");
Require(!DeviceConfigurationReadPolicy.CanReadOnDemand(storageManage, null, out _),
    "Uma operação destrutiva entrou no fluxo de leitura.");
Require(!DeviceConfigurationReadPolicy.CanReadOnDemand(whiteLight, false, out _),
    "A camada tentou ler um recurso negado pela própria câmera.");
Require(DeviceConfigurationReadPolicy.CanReadOnDemand(whiteLight, true, out _),
    "Uma leitura explicitamente suportada foi recusada.");
Console.WriteLine("DEVICE_CONFIGURATION_READ_POLICY_OK");

Require(DeviceSettingsPresentationPolicy.ShowConfirmed(human),
    "Um recurso confirmado não apareceu na configuração individual.");
Require(!DeviceSettingsPresentationPolicy.ShowConfirmed(wifi),
    "Um recurso incompatível apareceu como utilizável.");
Require(!DeviceSettingsPresentationPolicy.ShowConfirmed(null),
    "Um recurso ainda desconhecido apareceu como utilizável.");
Require(DeviceSettingsPresentationPolicy.OfferSafeProbe("Storage.Info") &&
        DeviceSettingsPresentationPolicy.OfferSafeProbe("Recording.Main") &&
        !DeviceSettingsPresentationPolicy.OfferSafeProbe("Network.Wifi"),
    "A tela ofereceu uma sondagem fora da lista segura.");
Console.WriteLine("DEVICE_SETTINGS_PRESENTATION_POLICY_OK");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
