namespace XMEyeCloudTester;

/// <summary>
/// Keeps the per-camera settings screen honest: a feature is shown as usable
/// only after evidence from that specific device/firmware.
/// </summary>
internal static class DeviceSettingsPresentationPolicy
{
    internal static bool ShowConfirmed(DeviceProfileStore.ConfigurationBinding? binding) =>
        binding?.Supported == true;

    internal static bool OfferSafeProbe(string configurationKey) =>
        configurationKey is "Storage.Info" or "Recording.Main";
}
