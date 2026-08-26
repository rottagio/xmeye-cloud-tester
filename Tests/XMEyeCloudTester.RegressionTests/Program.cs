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

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
