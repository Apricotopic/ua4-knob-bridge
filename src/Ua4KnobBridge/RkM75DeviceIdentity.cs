namespace Ua4KnobBridge;

/// <summary>
/// Identifies only the RK M75 Consumer Control top-level collection in its
/// supported wired and 2.4 GHz receiver modes. Other keyboards and media
/// devices are deliberately excluded before report parsing begins.
/// </summary>
public static class RkM75DeviceIdentity
{
    private static readonly string[] SupportedDeviceIds =
    [
        "vid_258a&pid_0163", // RK M75 wired USB
        "vid_258a&pid_0150"  // RK M75 2.4 GHz receiver
    ];

    public static bool IsConsumerControlCollection(string? devicePath) =>
        devicePath is not null &&
        SupportedDeviceIds.Any(id => devicePath.Contains(id, StringComparison.OrdinalIgnoreCase)) &&
        devicePath.Contains("mi_01", StringComparison.OrdinalIgnoreCase) &&
        devicePath.Contains("col02", StringComparison.OrdinalIgnoreCase);
}
