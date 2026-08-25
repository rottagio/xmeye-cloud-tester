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
        public List<int> KnownChannels { get; set; } = [];
        // null acompanha a qualidade global; true = SD; false = HD.
        public bool? PreferredSd { get; set; }
        // Espelha somente a exibição no computador; não altera a câmera.
        public bool MirrorDisplay { get; set; }
        public bool IsManual { get; set; }
        public bool IsNetworkDevice { get; set; }
        public string Identifier { get; set; } = string.Empty;
        public int NetworkPort { get; set; } = 34567;
        public string DeviceUser { get; set; } = string.Empty;
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
