using System.IO;
using System.Text.Json;

namespace XMEyeCloudTester;

internal sealed class DeviceReadOnlyConfigStore
{
    internal sealed class PartitionInfo
    {
        public int TypeCode { get; set; }
        public uint TotalMegabytes { get; set; }
        public uint FreeMegabytes { get; set; }
        public int StatusCode { get; set; }
        public string FileSystem { get; set; } = string.Empty;
    }

    internal sealed class StorageInfo
    {
        public DateTime ObservedAtUtc { get; set; }
        public int DiskCount { get; set; }
        public List<PartitionInfo> Partitions { get; set; } = [];
    }

    internal sealed class RecordingInfo
    {
        public DateTime ObservedAtUtc { get; set; }
        public int Channel { get; set; }
        public int PreRecordSeconds { get; set; }
        public bool Redundancy { get; set; }
        public int PacketLengthMinutes { get; set; }
        public int RecordModeCode { get; set; }
        public bool UsesSata { get; set; }
        public bool UsesUsb { get; set; }
        public bool UsesSd { get; set; }
        public bool UsesDvd { get; set; }
        public int EnabledSchedulePeriods { get; set; }
    }

    internal sealed class LightInfo
    {
        public DateTime ObservedAtUtc { get; set; }
        public int Channel { get; set; }
        public string WorkMode { get; set; } = string.Empty;
        public int DurationSeconds { get; set; }
        public int Level { get; set; }
        public bool ScheduleEnabled { get; set; }
        public int ScheduleStartSeconds { get; set; }
        public int ScheduleEndSeconds { get; set; }
    }

    internal sealed class DeviceData
    {
        public StorageInfo? Storage { get; set; }
        public Dictionary<int, RecordingInfo> RecordingByChannel { get; set; } = [];
        public Dictionary<int, LightInfo> LightByChannel { get; set; } = [];
    }

    public Dictionary<string, DeviceData> Devices { get; set; } = new(StringComparer.Ordinal);

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XMEyeCloudAccountTester", "device-readonly-config.json");

    internal static DeviceReadOnlyConfigStore Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new DeviceReadOnlyConfigStore();
            return JsonSerializer.Deserialize<DeviceReadOnlyConfigStore>(File.ReadAllText(StorePath))
                ?? new DeviceReadOnlyConfigStore();
        }
        catch
        {
            return new DeviceReadOnlyConfigStore();
        }
    }

    internal DeviceData GetOrCreate(string deviceKey)
    {
        if (!Devices.TryGetValue(deviceKey, out DeviceData? data))
        {
            data = new DeviceData();
            Devices[deviceKey] = data;
        }
        return data;
    }

    internal void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            string temporary = StorePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporary, StorePath, overwrite: true);
        }
        catch
        {
            // A leitura continua válida na sessão mesmo se o cache não puder ser persistido.
        }
    }
}
