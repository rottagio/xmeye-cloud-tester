namespace XMEyeCloudTester;

/// <summary>
/// Deliberately small allow-list for remote writes. A catalog entry being
/// writable is not enough: each released editor must be explicitly enabled.
/// </summary>
internal static class DeviceConfigurationWritePolicy
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(2);

    internal static bool CanWrite(
        DeviceConfigurationCatalog.Definition definition,
        DeviceProfileStore.ConfigurationBinding? binding,
        bool hasFreshSnapshot,
        DateTime utcNow,
        out TimeSpan wait,
        out string reason)
    {
        wait = TimeSpan.Zero;
        if (definition.Key != "Light.White")
        {
            reason = "Esta configuração ainda não foi liberada para alteração remota.";
            return false;
        }
        if (definition.Access != DeviceConfigurationCatalog.AccessMode.ReadWrite ||
            definition.WriteCommand is null ||
            definition.Risk is DeviceConfigurationCatalog.RiskLevel.Destructive or
                DeviceConfigurationCatalog.RiskLevel.SensitiveWrite)
        {
            reason = "O catálogo não permite alteração controlada deste item.";
            return false;
        }
        if (binding?.Supported != true)
        {
            reason = "A câmera não confirmou suporte a esta configuração.";
            return false;
        }
        if (!hasFreshSnapshot)
        {
            reason = "É necessário ler e validar o valor atual antes de alterar.";
            return false;
        }
        if (binding.LastWriteAtUtc is DateTime lastWrite)
        {
            DateTime next = lastWrite + MinimumInterval;
            if (next > utcNow)
            {
                wait = next - utcNow;
                reason = "Aguarde o intervalo de proteção entre alterações.";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }

    internal static bool IsValidWhiteLightLevel(int value) => value is >= 0 and <= 100;
}
