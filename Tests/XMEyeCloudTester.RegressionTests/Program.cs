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
Require(new CameraCatalogStore.Entry().PtzSpeed == 5,
    "A velocidade PTZ padrão deixou de preservar o comportamento já publicado.");

Console.WriteLine("CHANNEL_MIGRATION_REGRESSION_OK");

Require(ConnectionRecoveryPolicy.ErrorCooldown(-27, 1) == TimeSpan.FromHours(1),
    "O bloqueio -27 não preservou a quarentena de uma hora.");
Require(ConnectionRecoveryPolicy.ErrorCooldown(-25, 1) == TimeSpan.FromMinutes(10),
    "O limite -25 não recebeu a pausa de dez minutos.");
Require(ConnectionRecoveryPolicy.ErrorCooldown(-8, 1) == TimeSpan.FromMinutes(10),
    "O erro -8 não preservou o mínimo de dez minutos.");
Require(ConnectionRecoveryPolicy.ErrorCooldown(-7, 3) == TimeSpan.FromMinutes(1),
    "A falha transitória passou a deixar a câmera sem tentativa por vários minutos.");
Require(ConnectionRecoveryPolicy.PassiveGrace(unstable: true) == TimeSpan.FromSeconds(15),
    "O modo instável não recebeu a janela passiva ampliada.");
Require(ConnectionRecoveryPolicy.PassiveCycleMinimum(true, 60) == TimeSpan.FromMinutes(5),
    "O modo instável não limitou os ciclos passivos.");
Require(ConnectionRecoveryPolicy.DeviceLoginMinimum(true, 60) == TimeSpan.FromMinutes(1),
    "O modo instável deixou o dispositivo sem tentativa por mais de um minuto.");
Require(ConnectionRecoveryPolicy.DeviceLoginMinimum(false, 30) == TimeSpan.FromMinutes(1),
    "O login normal não preservou o intervalo mínimo de um minuto.");
Require(ConnectionRecoveryPolicy.PreviewSpacing == TimeSpan.FromSeconds(3),
    "A restauração sequencial dos canais perdeu o espaçamento de proteção.");
Require(ConnectionRecoveryPolicy.ChannelRetryDelay(15) == TimeSpan.FromMinutes(1) &&
        ConnectionRecoveryPolicy.ChannelRetryDelay(300) == TimeSpan.FromMinutes(5),
    "A repetição protegida do canal perdeu o intervalo configurado.");
Require(!ConnectionRecoveryPolicy.PreserveCooldownAfterPositiveMonitorCallback(-25) &&
        ConnectionRecoveryPolicy.PreserveCooldownAfterPositiveMonitorCallback(-27),
    "O callback online não distingue limite transitório de bloqueio do dispositivo.");
Console.WriteLine("CONNECTION_RECOVERY_POLICY_OK");

Require(new AppPreferences().AutoReconnect,
    "Novas instalações deixaram de ativar a recuperação protegida de canais.");
var legacyPreferences = new AppPreferences { AutoReconnect = false, RecoveryPolicyVersion = 0 };
Require(AppPreferences.ApplyRecoveryPolicyMigration(
            legacyPreferences, hasPersistedVersion: false) &&
        legacyPreferences.AutoReconnect && legacyPreferences.RecoveryPolicyVersion == 1,
    "Uma preferência legada deixou a recuperação automática desativada.");
var currentPreferences = new AppPreferences { AutoReconnect = false, RecoveryPolicyVersion = 1 };
Require(!AppPreferences.ApplyRecoveryPolicyMigration(
            currentPreferences, hasPersistedVersion: true) &&
        !currentPreferences.AutoReconnect,
    "Uma escolha atual do usuário foi sobrescrita pela migração.");
Console.WriteLine("AUTO_RECOVERY_DEFAULT_OK");

Require(QtRuntime.IntervalFor(QtPumpState.Active) == TimeSpan.FromMilliseconds(25) &&
        QtRuntime.IntervalFor(QtPumpState.VisibleIdle) == TimeSpan.FromMilliseconds(100) &&
        QtRuntime.IntervalFor(QtPumpState.MinimizedIdle) == TimeSpan.FromMilliseconds(250),
    "O bombeamento adaptativo do Qt perdeu os intervalos definidos.");
Console.WriteLine("QT_EVENT_PUMP_POLICY_OK");

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
Require(DeviceSettingsPresentationPolicy.CustomerSections.SequenceEqual(
        ["Básicas", "Armazenamento", "Gravação", "Alarme inteligente",
         "Som e luz", "Áudio", "Rede", "Avançadas", "Sobre"]),
    "As seções do menu deixaram de acompanhar a experiência do aplicativo móvel.");
Require(DeviceSettingsPresentationPolicy.IsCustomerFacingConfiguration("Alarm.Human") &&
        DeviceSettingsPresentationPolicy.IsCustomerFacingConfiguration("Network.Wifi") &&
        !DeviceSettingsPresentationPolicy.IsCustomerFacingConfiguration("Network.Rtsp") &&
        !DeviceSettingsPresentationPolicy.IsCustomerFacingConfiguration("Ptz.Presets") &&
        !DeviceSettingsPresentationPolicy.IsCustomerFacingConfiguration("Firmware.Upgrade"),
    "Uma opção técnica voltou ao menu comum ou uma configuração do cliente foi removida.");
Console.WriteLine("DEVICE_SETTINGS_PRESENTATION_POLICY_OK");

DeviceConfigurationCatalog.Definition lightDefinition =
    DeviceConfigurationCatalog.Find("Light.White")!;
DeviceConfigurationCatalog.Definition networkDefinition =
    DeviceConfigurationCatalog.Find("Network.Wifi")!;
var lightBinding = new DeviceProfileStore.ConfigurationBinding { Supported = true };
Require(DeviceConfigurationWritePolicy.CanWrite(
        lightDefinition, lightBinding, true, DateTime.UtcNow, out _, out _),
    "A alteração controlada de iluminação confirmada foi recusada.");
Require(!DeviceConfigurationWritePolicy.CanWrite(
        networkDefinition, new DeviceProfileStore.ConfigurationBinding { Supported = true },
        true, DateTime.UtcNow, out _, out _),
    "Uma alteração sensível de rede foi liberada.");
Require(!DeviceConfigurationWritePolicy.CanWrite(
        lightDefinition, new DeviceProfileStore.ConfigurationBinding { Supported = false },
        true, DateTime.UtcNow, out _, out _),
    "Uma câmera incompatível recebeu permissão de escrita.");
lightBinding.LastWriteAtUtc = DateTime.UtcNow;
Require(!DeviceConfigurationWritePolicy.CanWrite(
        lightDefinition, lightBinding, true, DateTime.UtcNow, out TimeSpan writeWait, out _) &&
        writeWait > TimeSpan.FromSeconds(1),
    "O intervalo entre alterações remotas não foi aplicado.");
Require(DeviceConfigurationWritePolicy.CanWrite(
        networkDefinition, new DeviceProfileStore.ConfigurationBinding { Supported = true },
        true, true, DateTime.UtcNow, out _, out _),
    "Explicitly confirmed sensitive writes must be allowed.");
Require(DeviceConfigurationWritePolicy.IsValidWhiteLightLevel(0) &&
        DeviceConfigurationWritePolicy.IsValidWhiteLightLevel(100) &&
        !DeviceConfigurationWritePolicy.IsValidWhiteLightLevel(101),
    "A validação do nível de iluminação foi alterada.");
Console.WriteLine("DEVICE_CONFIGURATION_WRITE_POLICY_OK");

var basicGeneral = new byte[CameraBasicConfigurationCodec.GeneralSize];
var basicLocation = new byte[CameraBasicConfigurationCodec.LocationSize];
var basicCamera = new byte[CameraBasicConfigurationCodec.CameraParameterSize];
var basicVolume = new byte[CameraBasicConfigurationCodec.VolumeSize];
var basicTimeZone = new byte[CameraBasicConfigurationCodec.TimeZoneSize];
var basicTime = new byte[CameraBasicConfigurationCodec.TimeSize];
WriteNativeString(basicGeneral, 0x0C, 0x40, "Corredor");
WriteNativeString(basicLocation, 0x08, 0x20, "Portuguese");
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(basicCamera.AsSpan(0x28, 4), 1);
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(basicCamera.AsSpan(0x2C, 4), 0);
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(basicCamera.AsSpan(0x3C, 4), 5);
basicVolume[1] = 1;
WriteNativeString(basicVolume, 0xA04, 0x20, "Single");
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(basicVolume.AsSpan(0xA24, 4), 50);
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(basicVolume.AsSpan(0xA28, 4), 50);
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(basicTimeZone.AsSpan(0, 4), 180);
int[] timeParts = [2026, 8, 27, 4, 16, 43, 30, 0];
for (int index = 0; index < timeParts.Length; index++)
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
        basicTime.AsSpan(index * 4, 4), timeParts[index]);

Require(CameraBasicConfigurationCodec.TryCreateSnapshot(
        basicGeneral, basicLocation, basicCamera, basicVolume, basicTimeZone, basicTime,
        out CameraBasicConfigurationCodec.Snapshot? basicSnapshot, out string basicError) &&
    basicSnapshot is not null,
    $"O codec tipado não leu uma resposta básica válida: {basicError}");
CameraBasicConfigurationCodec.Snapshot verifiedBasic = basicSnapshot!;
Require(verifiedBasic.MachineName == "Corredor" && verifiedBasic.Language == "Portuguese" &&
        verifiedBasic.PictureFlip && !verifiedBasic.PictureMirror &&
        verifiedBasic.DayNightSensitivity == 5 && verifiedBasic.LeftVolume == 50 &&
        verifiedBasic.RightVolume == 50 && verifiedBasic.MinutesWest == 180,
    "O codec tipado interpretou um campo básico no offset errado.");
byte[] renamed = CameraBasicConfigurationCodec.WithMachineName(verifiedBasic.General, "Entrada");
Require(renamed.AsSpan(0, 0x0C).SequenceEqual(verifiedBasic.General.AsSpan(0, 0x0C)) &&
        renamed.AsSpan(0x4C).SequenceEqual(verifiedBasic.General.AsSpan(0x4C)),
    "A atualização do nome alterou bytes fora do campo comprovado.");
byte[] changedCamera = CameraBasicConfigurationCodec.WithCameraParameters(
    verifiedBasic.CameraParameters, true, false, 7);
Require(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(changedCamera.AsSpan(0x2C, 4)) == 1 &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(changedCamera.AsSpan(0x28, 4)) == 0 &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(changedCamera.AsSpan(0x3C, 4)) == 7,
    "A cópia de parâmetros básicos não alterou somente os campos solicitados.");
Require(CameraBasicConfigurationCodec.GeneralType == 0x103ED &&
        CameraBasicConfigurationCodec.LocationType == 0x103EE &&
        CameraBasicConfigurationCodec.CameraParameterType == 0x5E &&
        CameraBasicConfigurationCodec.VolumeType == 0x1F8,
    "Os seletores tipados confirmados do VMS foram alterados.");
Console.WriteLine("TYPED_BASIC_CONFIGURATION_CODEC_OK");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void WriteNativeString(byte[] buffer, int offset, int length, string value)
{
    byte[] encoded = System.Text.Encoding.UTF8.GetBytes(value);
    encoded.CopyTo(buffer.AsSpan(offset, length));
}
