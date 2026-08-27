using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XMEyeCloudTester;

/// <summary>
/// Local, read-only inventory of what each device has reported. The store never
/// initiates a device request; callers feed it metadata already returned by the
/// account service, the CMS registry or an explicitly requested capability read.
/// </summary>
internal sealed class DeviceProfileStore
{
    internal enum CapabilityState
    {
        Unknown,
        Available,
        Unavailable
    }

    internal sealed record CapabilitySnapshot(
        CapabilityState State,
        string Source,
        DateTime? ObservedAtUtc);

    internal sealed class CapabilityEvidence
    {
        public bool Supported { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTime ObservedAtUtc { get; set; }
        public string Firmware { get; set; } = string.Empty;
    }

    internal sealed class ConfigurationBinding
    {
        public bool? Supported { get; set; }
        public string Section { get; set; } = string.Empty;
        public string JsonName { get; set; } = string.Empty;
        public int? ReadCommand { get; set; }
        public int? WriteCommand { get; set; }
        public string Scope { get; set; } = string.Empty;
        public string Access { get; set; } = string.Empty;
        public string Risk { get; set; } = string.Empty;
        public string RequiredCapability { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
        public DateTime? ObservedAtUtc { get; set; }
        public DateTime? LastWriteAtUtc { get; set; }
        public string Firmware { get; set; } = string.Empty;
    }

    internal sealed class Profile
    {
        public string ReportedModel { get; set; } = string.Empty;
        public string Firmware { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string OemId { get; set; } = string.Empty;
        public string FirmwareProductCode { get; set; } = string.Empty;
        public string OemVendorCode { get; set; } = string.Empty;
        public string ChipSolutionCode { get; set; } = string.Empty;
        public string CompilationDirectoryCode { get; set; } = string.Empty;
        public string ChipFamily { get; set; } = string.Empty;
        public int? ConfirmedChannelCount { get; set; }
        public string ChannelCountSource { get; set; } = string.Empty;
        public Dictionary<string, CapabilityEvidence> Capabilities { get; set; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, ConfigurationBinding> CompatibleCommands { get; set; } =
            new(StringComparer.Ordinal);
        public DateTime UpdatedAtUtc { get; set; }
    }

    public Dictionary<string, Profile> Devices { get; set; } = new(StringComparer.Ordinal);

    private static readonly Regex FirmwareProductCodePattern = new(
        @"(?:^|\.)([0-9A-Fa-f]{8})(?:\.|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> OfficialChipFamilies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["00"] = "TI",
            ["01"] = "HiSilicon 16M flash",
            ["02"] = "HiSilicon 8M flash",
            ["03"] = "TI (nova criptografia)",
            ["04"] = "Ambarella",
            ["05"] = "HiSilicon 16M (linha residencial)",
            ["06"] = "HiSilicon 3518E 8M",
            ["07"] = "HiSilicon 3518E XM"
        };

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XMEyeCloudAccountTester",
        "device-profiles.json");

    internal static DeviceProfileStore Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new DeviceProfileStore();
            DeviceProfileStore? loaded = JsonSerializer.Deserialize<DeviceProfileStore>(
                File.ReadAllText(StorePath));
            return loaded ?? new DeviceProfileStore();
        }
        catch
        {
            return new DeviceProfileStore();
        }
    }

    internal Profile GetOrCreate(string deviceKey)
    {
        if (!Devices.TryGetValue(deviceKey, out Profile? profile))
        {
            profile = new Profile();
            Devices[deviceKey] = profile;
        }
        return profile;
    }

    internal bool Remove(string deviceKey) => Devices.Remove(deviceKey);

    internal bool UpdateIdentity(
        CloudApi.AccountDevice device, CameraCatalogStore.Entry catalogEntry, string oemId = "")
    {
        Profile profile = GetOrCreate(device.CloudId);
        string previousFirmware = profile.Firmware;
        bool changed = SetIfPresent(profile.ReportedModel, device.Model, out string reportedModel);
        profile.ReportedModel = reportedModel;
        changed |= SetIfPresent(profile.Firmware, device.Firmware, out string firmware);
        profile.Firmware = firmware;
        changed |= SetIfPresent(profile.ProductId, device.ProductId, out string productId);
        profile.ProductId = productId;
        changed |= SetIfPresent(profile.OemId, oemId, out string reportedOemId);
        profile.OemId = reportedOemId;

        int? confirmedChannels = catalogEntry.ChannelCountOverride is 1 or 2
            ? catalogEntry.ChannelCountOverride
            : catalogEntry.DetectedChannelCount is 1 or 2
                ? catalogEntry.DetectedChannelCount
                : null;
        string channelSource = catalogEntry.ChannelCountOverride is 1 or 2
            ? "configuração confirmada pelo usuário"
            : catalogEntry.DetectedChannelCount is 1 or 2
                ? "previews com imagem confirmada"
                : string.Empty;
        if (confirmedChannels is int count &&
            (profile.ConfirmedChannelCount != count || profile.ChannelCountSource != channelSource))
        {
            profile.ConfirmedChannelCount = count;
            profile.ChannelCountSource = channelSource;
            changed = true;
        }
        else if (confirmedChannels is null &&
                 string.Equals(profile.ChannelCountSource, "previews com imagem confirmada", StringComparison.Ordinal))
        {
            profile.ConfirmedChannelCount = null;
            profile.ChannelCountSource = string.Empty;
            changed = true;
        }

        changed |= ApplyFirmwareParts(profile);
        if (!string.Equals(previousFirmware, profile.Firmware, StringComparison.Ordinal) &&
            previousFirmware.Length > 0)
        {
            // Capabilities are firmware-specific. A firmware change invalidates
            // old evidence instead of silently applying it to a different build.
            profile.Capabilities.Clear();
            profile.CompatibleCommands.Clear();
            changed = true;
        }
        changed |= RebuildConfigurationBindings(profile);
        if (changed)
            profile.UpdatedAtUtc = DateTime.UtcNow;
        return changed;
    }

    internal bool RecordCapability(
        string deviceKey, string capability, bool supported, string source)
    {
        Profile profile = GetOrCreate(deviceKey);
        if (profile.Capabilities.TryGetValue(capability, out CapabilityEvidence? current) &&
            current.Supported == supported &&
            string.Equals(current.Source, source, StringComparison.Ordinal) &&
            string.Equals(current.Firmware, profile.Firmware, StringComparison.Ordinal))
            return false;

        profile.Capabilities[capability] = new CapabilityEvidence
        {
            Supported = supported,
            Source = source,
            ObservedAtUtc = DateTime.UtcNow,
            Firmware = profile.Firmware
        };
        RebuildConfigurationBindings(profile);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    internal bool RecordConfigurationEvidence(
        string deviceKey, string configurationKey, bool supported, string source,
        DateTime? observedAtUtc = null)
    {
        Profile profile = GetOrCreate(deviceKey);
        RebuildConfigurationBindings(profile);
        if (!profile.CompatibleCommands.TryGetValue(configurationKey, out ConfigurationBinding? binding))
            return false;
        if (binding.Supported == supported &&
            string.Equals(binding.Evidence, source, StringComparison.Ordinal) &&
            string.Equals(binding.Firmware, profile.Firmware, StringComparison.Ordinal))
            return false;
        binding.Supported = supported;
        binding.Evidence = source;
        binding.ObservedAtUtc = observedAtUtc ?? DateTime.UtcNow;
        binding.Firmware = profile.Firmware;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        return true;
    }

    internal bool RebuildConfigurationBindings(string deviceKey) =>
        RebuildConfigurationBindings(GetOrCreate(deviceKey));

    internal bool RecordConfigurationWrite(
        string deviceKey, string configurationKey, DateTime observedAtUtc)
    {
        Profile profile = GetOrCreate(deviceKey);
        RebuildConfigurationBindings(profile);
        if (!profile.CompatibleCommands.TryGetValue(configurationKey, out ConfigurationBinding? binding) ||
            binding.LastWriteAtUtc == observedAtUtc)
            return false;
        binding.LastWriteAtUtc = observedAtUtc;
        profile.UpdatedAtUtc = observedAtUtc;
        return true;
    }

    internal bool TryGetCurrentCapability(
        string deviceKey, string capability, out bool supported)
    {
        supported = false;
        if (!Devices.TryGetValue(deviceKey, out Profile? profile) ||
            !profile.Capabilities.TryGetValue(capability, out CapabilityEvidence? evidence) ||
            !string.Equals(evidence.Firmware, profile.Firmware, StringComparison.Ordinal))
            return false;
        supported = evidence.Supported;
        return true;
    }

    internal CapabilitySnapshot GetCapability(string deviceKey, params string[] capabilities)
    {
        if (!Devices.TryGetValue(deviceKey, out Profile? profile))
            return new CapabilitySnapshot(CapabilityState.Unknown, string.Empty, null);

        CapabilityEvidence[] current = capabilities
            .Where(profile.Capabilities.ContainsKey)
            .Select(key => profile.Capabilities[key])
            .Where(evidence => string.Equals(evidence.Firmware, profile.Firmware, StringComparison.Ordinal))
            .ToArray();
        if (current.Length == 0)
            return new CapabilitySnapshot(CapabilityState.Unknown, string.Empty, null);

        CapabilityEvidence[] decisive = current.Any(evidence => evidence.Supported)
            ? current.Where(evidence => evidence.Supported).ToArray()
            : current;
        return new CapabilitySnapshot(
            decisive.Any(evidence => evidence.Supported)
                ? CapabilityState.Available
                : CapabilityState.Unavailable,
            string.Join("; ", decisive.Select(evidence => evidence.Source)
                .Where(source => source.Length > 0).Distinct(StringComparer.Ordinal)),
            decisive.Max(evidence => (DateTime?)evidence.ObservedAtUtc));
    }

    internal string BuildTechnicalSummary(string deviceKey)
    {
        if (!Devices.TryGetValue(deviceKey, out Profile? profile))
            return string.Empty;
        var details = new List<string>();
        if (profile.ReportedModel.Length > 0)
            details.Add("Modelo informado: " + profile.ReportedModel);
        if (profile.Firmware.Length > 0)
            details.Add("Firmware: " + profile.Firmware);
        if (profile.FirmwareProductCode.Length > 0)
            details.Add("Código interno: " + profile.FirmwareProductCode);
        if (profile.ChipSolutionCode.Length > 0)
            details.Add("Plataforma: " +
                (profile.ChipFamily.Length > 0
                    ? $"{profile.ChipFamily} ({profile.ChipSolutionCode})"
                    : $"código {profile.ChipSolutionCode} ainda não catalogado"));
        if (profile.OemId.Length > 0)
            details.Add("OEM: " + profile.OemId);
        if (profile.ConfirmedChannelCount is int channelCount)
            details.Add($"Canais confirmados: {channelCount}");
        int confirmedCapabilities = profile.Capabilities.Count;
        if (confirmedCapabilities > 0)
            details.Add($"Capacidades registradas: {confirmedCapabilities}");
        int compatibleCommands = profile.CompatibleCommands.Count(item => item.Value.Supported == true);
        if (compatibleCommands > 0)
            details.Add($"Configurações compatíveis: {compatibleCommands}");
        return string.Join("  •  ", details);
    }

    internal void Save()
    {
        string? directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath = StorePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(temporaryPath, StorePath, overwrite: true);
    }

    private static bool ApplyFirmwareParts(Profile profile)
    {
        Match match = FirmwareProductCodePattern.Match(profile.Firmware);
        string productCode = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        string oem = productCode.Length == 8 ? productCode[..3] : string.Empty;
        string chip = productCode.Length == 8 ? productCode.Substring(3, 2) : string.Empty;
        string compilation = productCode.Length == 8 ? productCode[5..] : string.Empty;
        string family = OfficialChipFamilies.TryGetValue(chip, out string? knownFamily)
            ? knownFamily
            : string.Empty;
        bool changed = Set(profile.FirmwareProductCode, productCode, out string firmwareProductCode);
        profile.FirmwareProductCode = firmwareProductCode;
        changed |= Set(profile.OemVendorCode, oem, out string oemVendorCode);
        profile.OemVendorCode = oemVendorCode;
        changed |= Set(profile.ChipSolutionCode, chip, out string chipSolutionCode);
        profile.ChipSolutionCode = chipSolutionCode;
        changed |= Set(profile.CompilationDirectoryCode, compilation, out string compilationDirectoryCode);
        profile.CompilationDirectoryCode = compilationDirectoryCode;
        changed |= Set(profile.ChipFamily, family, out string chipFamily);
        profile.ChipFamily = chipFamily;
        return changed;
    }

    private static bool RebuildConfigurationBindings(Profile profile)
    {
        bool changed = false;
        foreach (DeviceConfigurationCatalog.Definition definition in DeviceConfigurationCatalog.Definitions)
        {
            bool? supported = null;
            string evidence = definition.Evidence;
            DateTime? observedAtUtc = null;
            if (definition.RequiredCapability.Length > 0 &&
                profile.Capabilities.TryGetValue(definition.RequiredCapability, out CapabilityEvidence? capability) &&
                string.Equals(capability.Firmware, profile.Firmware, StringComparison.Ordinal))
            {
                supported = capability.Supported;
                evidence = capability.Source;
                observedAtUtc = capability.ObservedAtUtc;
            }

            if (!profile.CompatibleCommands.TryGetValue(definition.Key, out ConfigurationBinding? binding))
            {
                binding = new ConfigurationBinding();
                profile.CompatibleCommands[definition.Key] = binding;
                changed = true;
            }

            // A resposta direta de um comando prevalece sobre a declaração
            // genérica de capacidade enquanto o firmware não mudar.
            bool retainDirectEvidence = binding.ObservedAtUtc is not null &&
                string.Equals(binding.Firmware, profile.Firmware, StringComparison.Ordinal) &&
                !binding.Evidence.StartsWith("SystemFunction", StringComparison.Ordinal);
            changed |= SetBinding(
                binding,
                definition,
                retainDirectEvidence ? binding.Supported : supported,
                retainDirectEvidence ? binding.Evidence : evidence,
                retainDirectEvidence ? binding.ObservedAtUtc : observedAtUtc,
                (retainDirectEvidence ? binding.ObservedAtUtc : observedAtUtc) is null
                    ? string.Empty
                    : profile.Firmware);
        }
        return changed;
    }

    private static bool SetBinding(
        ConfigurationBinding binding,
        DeviceConfigurationCatalog.Definition definition,
        bool? supported,
        string evidence,
        DateTime? observedAtUtc,
        string firmware)
    {
        bool changed = binding.Supported != supported ||
            binding.Section != definition.Section ||
            binding.JsonName != definition.JsonName ||
            binding.ReadCommand != definition.ReadCommand ||
            binding.WriteCommand != definition.WriteCommand ||
            binding.Scope != definition.Scope.ToString() ||
            binding.Access != definition.Access.ToString() ||
            binding.Risk != definition.Risk.ToString() ||
            binding.RequiredCapability != definition.RequiredCapability ||
            binding.Evidence != evidence ||
            binding.ObservedAtUtc != observedAtUtc ||
            binding.Firmware != firmware;
        if (!changed)
            return false;
        binding.Supported = supported;
        binding.Section = definition.Section;
        binding.JsonName = definition.JsonName;
        binding.ReadCommand = definition.ReadCommand;
        binding.WriteCommand = definition.WriteCommand;
        binding.Scope = definition.Scope.ToString();
        binding.Access = definition.Access.ToString();
        binding.Risk = definition.Risk.ToString();
        binding.RequiredCapability = definition.RequiredCapability;
        binding.Evidence = evidence;
        binding.ObservedAtUtc = observedAtUtc;
        binding.Firmware = firmware;
        return true;
    }

    private static bool SetIfPresent(string target, string candidate, out string value)
    {
        candidate = candidate.Trim();
        if (candidate.Length == 0)
        {
            value = target;
            return false;
        }
        return Set(target, candidate, out value);
    }

    private static bool Set(string target, string candidate, out string value)
    {
        if (string.Equals(target, candidate, StringComparison.Ordinal))
        {
            value = target;
            return false;
        }
        value = candidate;
        return true;
    }
}
