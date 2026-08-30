namespace XMEyeCloudTester;

internal static class ConnectionRecoveryPolicy
{
    internal static readonly TimeSpan DeviceBlockedCooldown = TimeSpan.FromHours(1);
    internal static readonly TimeSpan ConnectionLimitCooldown = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan UnstableObservationWindow = TimeSpan.FromHours(1);
    internal static readonly TimeSpan UnstableResetGap = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan UnstableModeDuration = TimeSpan.FromHours(1);
    internal static readonly TimeSpan NormalPassiveGrace = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan UnstablePassiveGrace = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PreviewSpacing = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan UnstableDeviceLoginMinimum = TimeSpan.FromMinutes(1);

    internal static TimeSpan ErrorCooldown(int error, int consecutiveFailures)
    {
        if (error == -27)
            return DeviceBlockedCooldown;
        if (error == -25)
            return ConnectionLimitCooldown;

        // -8/-7/-4 são falhas transitórias comuns durante a retomada P2P. Uma
        // única tentativa por dispositivo a cada minuto mantém a recuperação
        // ativa sem produzir rajadas no SDK. O VMS também volta a testar o
        // login do dispositivo; não deixa o preview parado por dez minutos.
        return TimeSpan.FromMinutes(1);
    }

    internal static TimeSpan PassiveGrace(bool unstable) =>
        unstable ? UnstablePassiveGrace : NormalPassiveGrace;

    internal static TimeSpan PassiveCycleMinimum(bool unstable, int configuredSeconds) =>
        unstable
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(Math.Max(60, configuredSeconds));

    internal static TimeSpan DeviceLoginMinimum(bool unstable, int configuredSeconds) =>
        unstable
            ? UnstableDeviceLoginMinimum
            : TimeSpan.FromSeconds(Math.Max(60, configuredSeconds));

    internal static TimeSpan ChannelRetryDelay(int configuredSeconds) =>
        TimeSpan.FromSeconds(Math.Max(60, configuredSeconds));

    internal static bool IsPersistentRequestError(int error) =>
        error is -27 or -25;

    internal static bool CurrentResultSupersedesPersistedError(
        int previousError, int currentResult) =>
        IsPersistentRequestError(previousError) &&
        !IsPersistentRequestError(currentResult);

}
