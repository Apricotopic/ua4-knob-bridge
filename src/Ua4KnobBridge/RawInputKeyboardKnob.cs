using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Ua4KnobBridge;

public sealed class RawInputKeyboardKnob : IKeyboardKnobInput
{
    private const uint RidInput = 0x10000003, RidiDeviceName = 0x20000007;
    private const uint RimTypeHid = 2, RidevRemove = 0x1, RidevInputSink = 0x100, RidevDevNotify = 0x2000;
    private const ushort UsagePageConsumer = 0x0C, UsageConsumerControl = 0x01;
    private readonly ConsumerUsageNormalizer normalizer = new();
    private nint targetWindow;

    public event EventHandler<KnobAction>? KnobActionReceived;
    public event EventHandler<string>? Diagnostic;
    public bool IsConnected { get; private set; }
    public string? DevicePath { get; private set; }

    public void Register(nint windowHandle)
    {
        targetWindow = windowHandle;
        var device = new RawInputDevice
        {
            UsagePage = UsagePageConsumer,
            Usage = UsageConsumerControl,
            Flags = RidevInputSink | RidevDevNotify,
            Target = targetWindow
        };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices");
        RefreshDevices();
    }

    public void RefreshDevices()
    {
        string? previousPath = DevicePath;
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, itemSize) == uint.MaxValue || count == 0)
        {
            IsConnected = false;
            DevicePath = null;
            normalizer.Reset();
            return;
        }
        var devices = new RawInputDeviceList[count];
        if (GetRawInputDeviceList(devices, ref count, itemSize) == uint.MaxValue) return;
        string? match = devices.Select(d => GetDeviceName(d.Device)).FirstOrDefault(RkM75DeviceIdentity.IsConsumerControlCollection);
        IsConnected = match is not null;
        DevicePath = match;
        if (!string.Equals(previousPath, match, StringComparison.OrdinalIgnoreCase))
            normalizer.Reset();
    }

    public void HandleRawInput(nint rawInputHandle)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawInputHandle, RidInput, null, ref size, headerSize) != 0 || size < headerSize + 8) return;
        byte[] buffer = new byte[size];
        if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) != size) return;
        if (BitConverter.ToUInt32(buffer, 0) != RimTypeHid) return;
        nint device = IntPtr.Size == 8 ? (nint)BitConverter.ToInt64(buffer, 8) : (nint)BitConverter.ToInt32(buffer, 8);
        string path = GetDeviceName(device);
        if (!RkM75DeviceIdentity.IsConsumerControlCollection(path)) return;
        IsConnected = true;
        DevicePath = path;
        int offset = (int)headerSize;
        uint reportSize = BitConverter.ToUInt32(buffer, offset);
        uint reportCount = BitConverter.ToUInt32(buffer, offset + 4);
        int dataOffset = offset + 8;
        for (uint index = 0; index < reportCount; index++)
        {
            int reportOffset = checked(dataOffset + (int)(index * reportSize));
            if (reportSize < 3 || reportOffset + reportSize > buffer.Length || buffer[reportOffset] != 0x02) continue;
            ushort usage = BitConverter.ToUInt16(buffer, reportOffset + 1);
            KnobAction? action = normalizer.Accept(usage);
            if (action.HasValue) KnobActionReceived?.Invoke(this, action.Value);
            else if (usage != 0 && usage is not ConsumerUsageNormalizer.ClockwiseUsage and not ConsumerUsageNormalizer.CounterClockwiseUsage)
                Diagnostic?.Invoke(this, $"Ignored RK consumer usage 0x{usage:X4}; report={Convert.ToHexString(buffer, reportOffset, (int)reportSize)}");
        }
    }

    private static string GetDeviceName(nint device)
    {
        uint chars = 0;
        _ = GetRawInputDeviceInfo(device, RidiDeviceName, null, ref chars);
        if (chars == 0) return "";
        var name = new StringBuilder((int)chars + 1);
        return GetRawInputDeviceInfo(device, RidiDeviceName, name, ref chars) == uint.MaxValue ? "" : name.ToString();
    }

    public void Dispose()
    {
        normalizer.Reset();
        if (targetWindow == 0) return;

        try
        {
            var device = new RawInputDevice
            {
                UsagePage = UsagePageConsumer,
                Usage = UsageConsumerControl,
                Flags = RidevRemove,
                Target = 0
            };
            _ = RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        catch { }
        finally { targetWindow = 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList { public nint Device; public uint Type; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nint WParam; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputDeviceList([In, Out] RawInputDeviceList[]? list, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(nint rawInput, uint command, [Out] byte[]? data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(nint device, uint command, StringBuilder? data, ref uint size);
}
