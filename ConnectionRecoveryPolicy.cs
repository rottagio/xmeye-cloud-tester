namespace XMEyeCloudTester;

internal static class ConnectionRecoveryPolicy
{
    internal static readonly TimeSpan DeviceBlockedCooldown = TimeSpan.FromHours(1);
    internal static readonly TimeSpan ConnectionLimitCooldown = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan UnstableObservationWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan UnstableModeDuration = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan NormalPassiveGrace = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan UnstablePassiveGrace = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PreviewSpacing = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan UnstableDeviceLoginMinimum = TimeSpan.FromMinutes(5);

    internal static TimeSpan ErrorCooldown(int error, int consecutiveFailures)
    {
        if (error == -27)
            return DeviceBlockedCooldown;
        if (error == -25)
            return ConnectionLimitCooldown;

        int exponent = Math.Min(4, Math.Max(0, consecutiveFailures - 1));
        int minutes = Math.Min(15, 1 << exponent);
        if (error == -8)
            minutes = Math.Max(minutes, 10);
        return TimeSpan.FromMinutes(minutes);
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
}
