using System.IO;
using System.Text.Json;

namespace XMEyeCloudTester;

internal sealed class CameraCatalogStore
{
    internal sealed class Entry
    {
        public string Name { get; set; } = string.Empty;
        public string OnlineName { get; set; } = string.Empty;
        public bool UseCustomName { get; set; }
        public string Group { get; set; } = "Casa";
        public int Order { get; set; } = int.MaxValue;
        public bool ShowInLiveView { get; set; } = true;
        // Permanece no catálogo, mas não é entregue ao motor CMS enquanto pausada.
        public bool Paused { get; set; }
        public List<int> KnownChannels { get; set; } = [];
        // null usa a descoberta automatica; 1 ou 2 respeita a quantidade
        // confirmada pelo usuario e impede que historico antigo crie canais.
        public int? ChannelCountOverride { get; set; }
        // Preenchido somente depois de validar o canal principal e testar o
        // secundario no mesmo ciclo. Nao depende do nome ou modelo da camera.
        public int? DetectedChannelCount { get; set; }
        // null acompanha a qualidade global; true = SD; false = HD.
        public bool? PreferredSd { get; set; }
        // Espelha somente a exibição no computador; não altera a câmera.
        public bool MirrorDisplay { get; set; }
        public bool IsManual { get; set; }
        public bool IsNetworkDevice { get; set; }
        public string Identifier { get; set; } = string.Empty;
        public int NetworkPort { get; set; } = 34567;
        public string DeviceUser { get; set; } = string.Empty;
        public bool MotionTracking { get; set; }
        public int MotionSensitivity { get; set; } = 3;
        public int TrackingSeconds { get; set; } = 15;
        public int TrackingPreset { get; set; }
        public bool HumanDetection { get; set; }
        public bool SmartAlert { get; set; }
        public bool ShowTrace { get; set; }
        public bool AudibleWarning { get; set; }
        public bool LightWarning { get; set; }
        public string TriggerMessage { get; set; } = "Movimento detectado";
        // A ausência da chave significa "capacidade ainda desconhecida".
        // Isso evita oferecer PTZ por tentativa em um canal que não o possui.
        public Dictionary<int, bool> PtzSupportedChannels { get; set; } = [];
        public Dictionary<int, bool> PtzMirrorChannels { get; set; } = [];
        public Dictionary<int, bool> PtzFlipChannels { get; set; } = [];
        // Erro -27: impede qualquer nova requisicao ao dispositivo durante uma
        // hora. Fica no catalogo para que fechar/reabrir o app nao contorne a
        // protecao e cause uma nova rajada de tentativas.
        public DateTime? RequestBlockedUntilUtc { get; set; }
        public int LastRequestError { get; set; }
    }

    public Dictionary<string, Entry> Cameras { get; set; } = new(StringComparer.Ordinal);

    private static string CatalogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XMEyeCloudAccountTester",
        "cameras.json");

    public static CameraCatalogStore Load()
    {
        try
        {
            if (!File.Exists(CatalogPath))
                return new CameraCatalogStore();
            CameraCatalogStore? loaded = JsonSerializer.Deserialize<CameraCatalogStore>(
                File.ReadAllText(CatalogPath));
            return loaded ?? new CameraCatalogStore();
        }
        catch
        {
            return new CameraCatalogStore();
        }
    }

    public Entry GetOrCreate(CloudApi.AccountDevice device, int suggestedOrder)
    {
        if (!Cameras.TryGetValue(device.CloudId, out Entry? entry))
        {
            entry = new Entry
            {
                Name = string.IsNullOrWhiteSpace(device.Alias) ? "Câmera" : device.Alias,
                OnlineName = device.Alias,
                Order = suggestedOrder
            };
            Cameras[device.CloudId] = entry;
        }
        return entry;
    }

    public void ApplyAndSort(List<CloudApi.AccountDevice> devices)
    {
        for (int index = 0; index < devices.Count; index++)
        {
            Entry entry = GetOrCreate(devices[index], index);
            string onlineName = devices[index].Alias.Trim();
            if (!string.IsNullOrWhiteSpace(onlineName))
                entry.OnlineName = onlineName;
            if (entry.UseCustomName && !string.IsNullOrWhiteSpace(entry.Name))
                devices[index].Alias = entry.Name.Trim();
            else if (!string.IsNullOrWhiteSpace(entry.OnlineName))
                devices[index].Alias = entry.OnlineName.Trim();
            devices[index].LocalGroup = string.IsNullOrWhiteSpace(entry.Group)
                ? "Casa"
                : entry.Group.Trim();
            devices[index].ShowInLiveView = entry.ShowInLiveView;
            devices[index].Paused = entry.Paused;
            if (entry.Paused)
                devices[index].RuntimeStatus = "Pausada";
        }
        devices.Sort((left, right) =>
        {
            int leftOrder = GetOrCreate(left, int.MaxValue).Order;
            int rightOrder = GetOrCreate(right, int.MaxValue).Order;
            int comparison = leftOrder.CompareTo(rightOrder);
            return comparison != 0
                ? comparison
                : string.Compare(left.Alias, right.Alias, StringComparison.CurrentCultureIgnoreCase);
        });
        NormalizeOrder(devices);
    }

    public void NormalizeOrder(IReadOnlyList<CloudApi.AccountDevice> devices)
    {
        for (int index = 0; index < devices.Count; index++)
            GetOrCreate(devices[index], index).Order = index;
    }

    public bool MarkChannelAvailable(CloudApi.AccountDevice device, int channel)
    {
        Entry entry = GetOrCreate(device, int.MaxValue);
        if (entry.KnownChannels.Contains(channel))
            return false;
        entry.KnownChannels.Add(channel);
        entry.KnownChannels.Sort();
        return true;
    }

    public bool SetDetectedChannelCount(CloudApi.AccountDevice device, int count)
    {
        count = Math.Clamp(count, 1, 2);
        Entry entry = GetOrCreate(device, int.MaxValue);
        bool changed = entry.DetectedChannelCount != count;
        entry.DetectedChannelCount = count;
        if (count == 1)
            changed |= entry.KnownChannels.RemoveAll(channel => channel > 0) > 0;
        return changed;
    }

    public bool IsKnownChannel(CloudApi.AccountDevice device, int channel) =>
        GetOrCreate(device, int.MaxValue).KnownChannels.Contains(channel);

    public void Save()
    {
        string? directory = Path.GetDirectoryName(CatalogPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = CatalogPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, CatalogPath, overwrite: true);
    }
}
