using Ua4KnobBridge;

var normalizer = new ConsumerUsageNormalizer();
if (!RkM75DeviceIdentity.IsConsumerControlCollection(@"\\?\HID#VID_258A&PID_0163&MI_01&Col02#wired"))
    throw new Exception("Wired RK M75 Consumer Control identity was not accepted.");
if (!RkM75DeviceIdentity.IsConsumerControlCollection(@"\\?\HID#VID_258A&PID_0150&MI_01&Col02#receiver"))
    throw new Exception("2.4 GHz RK M75 Consumer Control identity was not accepted.");
if (RkM75DeviceIdentity.IsConsumerControlCollection(@"\\?\HID#VID_258A&PID_0150&MI_01&Col03#system") ||
    RkM75DeviceIdentity.IsConsumerControlCollection(@"\\?\HID#VID_1234&PID_5678&MI_01&Col02#unrelated"))
    throw new Exception("Unrelated RK or non-RK collections were accepted.");
if (normalizer.Accept(ConsumerUsageNormalizer.ClockwiseUsage) != KnobAction.Clockwise)
    throw new Exception("0x02FF mapping invalid.");
if (normalizer.Accept(ConsumerUsageNormalizer.ClockwiseUsage) is not null)
    throw new Exception("Repeated active clockwise report was not collapsed.");
if (normalizer.Accept(0) is not null ||
    normalizer.Accept(ConsumerUsageNormalizer.CounterClockwiseUsage) != KnobAction.CounterClockwise)
    throw new Exception("0x02FE mapping or neutral re-arm invalid.");
if (normalizer.Accept(ConsumerUsageNormalizer.CounterClockwiseUsage) is not null)
    throw new Exception("Repeated active counter-clockwise report was not collapsed.");
normalizer.Accept(0);
if (normalizer.Accept(0x00E9) is not null || normalizer.Accept(0x00EA) is not null || normalizer.Accept(0x00E2) is not null)
    throw new Exception("Native Windows volume/mute usage was accepted.");

using var controller = new Ua4VendorVolumeController();
controller.Refresh();
Console.WriteLine("INPUT PASS: wired/2.4 GHz RK Consumer collections accepted; only 0x02FE/0x02FF accepted; repeated active reports collapsed; native E9/EA/E2 ignored");
Console.WriteLine($"UA4 connected={controller.IsConnected} volume={controller.CurrentVolume}");
Console.WriteLine($"UA4 path={controller.DevicePath}");
if (!controller.IsConnected) return 2;
if (controller.CurrentVolume is < 0 or > 100) throw new Exception("UA4 reported volume outside 0..100.");
Console.WriteLine("UA4 VENDOR PASS: device-side state was read through CTUAC2 vendor control.");

if (args.Contains("--exercise-live", StringComparer.OrdinalIgnoreCase))
{
    int original = controller.ReadVolume();
    try
    {
        if (controller.SetVolume(0) != 0 || controller.ReadVolume() != 0)
            throw new Exception("UA4 lower limit verification failed.");
        if (controller.SetVolume(100) != 100 || controller.ReadVolume() != 100)
            throw new Exception("UA4 upper limit verification failed.");

        if (controller.SetVolume(original) != original)
            throw new Exception("UA4 initial-volume restore failed.");
        for (int index = 0; index < 20; index++)
        {
            int up = Math.Min(original + 1, 100);
            if (controller.ChangeVolume(1) != up || controller.ReadVolume() != up)
                throw new Exception($"UA4 +1 verification failed during rapid sequence {index + 1}.");
            if (controller.ChangeVolume(-1) != original || controller.ReadVolume() != original)
                throw new Exception($"UA4 -1 verification failed during rapid sequence {index + 1}.");
        }
        Console.WriteLine("LIVE PASS: limits 0/100 and 20 rapid +1/-1 device-side sequences verified.");
    }
    finally
    {
        int restored = controller.SetVolume(original);
        Console.WriteLine($"LIVE RESTORE: UA4 volume={restored}");
        if (restored != original) throw new Exception($"UA4 restore failed: expected {original}, read {restored}.");
    }
}

if (args.Contains("--fast-live", StringComparer.OrdinalIgnoreCase))
{
    int original = controller.ReadVolume();
    int delta = original < 100 ? 1 : -1;
    int expected = original + delta;
    try
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int changed = controller.ChangeVolume(delta);
        timer.Stop();
        Thread.Sleep(15);
        int readBack = controller.ReadVolume();
        if (changed != expected || readBack != expected)
            throw new Exception($"Fast-path UA4 change failed: expected {expected}, changed {changed}, read {readBack}.");
        Console.WriteLine($"FAST LIVE PASS: UA4 {original} -> {expected}; command path={timer.Elapsed.TotalMilliseconds:F1} ms.");
    }
    finally
    {
        int restored = controller.SetVolume(original);
        Console.WriteLine($"FAST LIVE RESTORE: UA4 volume={restored}");
        if (restored != original) throw new Exception($"Fast-path restore failed: expected {original}, read {restored}.");
    }
}
return 0;
