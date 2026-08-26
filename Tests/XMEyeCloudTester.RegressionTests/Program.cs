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

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
