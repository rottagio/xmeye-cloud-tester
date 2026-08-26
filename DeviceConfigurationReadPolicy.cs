namespace XMEyeCloudTester;

/// <summary>
/// Central guard for remote configuration reads. This policy does not perform
/// I/O; it prevents operation/write-only/destructive catalog entries from ever
/// reaching the read path.
/// </summary>
internal static class DeviceConfigurationReadPolicy
{
    internal static bool CanReadOnDemand(
        DeviceConfigurationCatalog.Definition definition,
        bool? requiredCapabilitySupported,
        out string reason)
    {
        if (definition.ReadCommand is null)
        {
            reason = "A configuração não possui comando de leitura.";
            return false;
        }
        if (definition.Access == DeviceConfigurationCatalog.AccessMode.Operation ||
            definition.Risk == DeviceConfigurationCatalog.RiskLevel.Destructive)
        {
            reason = "Operações não podem entrar no fluxo de leitura.";
            return false;
        }
        if (definition.RequiredCapability.Length > 0 && requiredCapabilitySupported == false)
        {
            reason = "A câmera informou que não oferece este recurso.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    internal static bool CanDiscoverAutomatically(DeviceConfigurationCatalog.Definition definition) =>
        definition.Risk == DeviceConfigurationCatalog.RiskLevel.SafeRead &&
        definition.Access == DeviceConfigurationCatalog.AccessMode.ReadOnly &&
        definition.RequiredCapability.Length == 0 &&
        definition.Key == "Capability.SystemFunction";
}
