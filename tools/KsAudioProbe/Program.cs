using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static readonly Guid KsCategoryAudio = new("6994AD04-93EF-11D0-A3CC-00A0C9223196");
    private static readonly Guid KsPropSetTopology = new("720D4AC0-7533-11D0-A5D6-28DB04C10000");
    private static readonly Guid KsPropSetAudio = new("45FFAAA0-6E1B-11D0-BCF2-444553540000");
    private static readonly Guid KsNodeTypeVolume = new("3A5ACC00-C557-11D0-8A2B-00A0C9255AC1");
    private static readonly Guid KsNodeTypeMute = new("02B223C0-C557-11D0-8A2B-00A0C9255AC1");
    private const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint IoctlKsProperty = 0x002F0003;
    private const uint KsGet = 1, KsSet = 2, KsBasicSupport = 0x200, KsTopology = 0x10000000;
    private static bool roundTrip;

    public static int Main(string[] args)
    {
        roundTrip = args.Any(a => a.Equals("--roundtrip", StringComparison.OrdinalIgnoreCase));
        Guid category = KsCategoryAudio;
        IntPtr set = SetupDiGetClassDevs(ref category, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref category, index, ref iface))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                string path = GetPath(set, ref iface);
                if (!path.Contains("vid_20b1&pid_3026", StringComparison.OrdinalIgnoreCase)) continue;
                Probe(path);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return 0;
    }

    private static void Probe(string path)
    {
        Console.WriteLine($"PATH {path}");
        using SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"OPEN ERROR {Marshal.GetLastWin32Error()}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return;
        }
        byte[] topologyRequest = PropertyRequest(KsPropSetTopology, 1, KsGet, null, null);
        byte[] nodesData = Ioctl(handle, topologyRequest, 65536, "KSPROPERTY_TOPOLOGY_NODES");
        if (nodesData.Length < 8) return;
        int size = BitConverter.ToInt32(nodesData, 0), count = BitConverter.ToInt32(nodesData, 4);
        Console.WriteLine($"NODES size={size} count={count}");
        for (int node = 0; node < count && 8 + node * 16 + 16 <= nodesData.Length; node++)
        {
            Guid type = new(nodesData.AsSpan(8 + node * 16, 16));
            Console.WriteLine($"  NODE {node} {type}{(type == KsNodeTypeVolume ? " VOLUME" : type == KsNodeTypeMute ? " MUTE" : "")}");
            if (type == KsNodeTypeVolume)
            {
                int[] current = new int[2];
                for (int channel = 0; channel < 3; channel++)
                {
                    byte[] request = PropertyRequest(KsPropSetAudio, 4, KsGet | KsTopology, node, channel);
                    int? value = TryIoctlInt(handle, request, $"VOLUME node={node} channel={channel}");
                    if (channel < 2 && value.HasValue) current[channel] = value.Value;
                    byte[] support = PropertyRequest(KsPropSetAudio, 4, KsBasicSupport | KsTopology, node, channel);
                    TryIoctlHex(handle, support, $"VOLUME_SUPPORT node={node} channel={channel}");
                }
                if (roundTrip) RoundTrip(handle, node, current);
            }
            if (type == KsNodeTypeMute)
            {
                for (int channel = 0; channel < 3; channel++)
                    _ = TryIoctlInt(handle, PropertyRequest(KsPropSetAudio, 13, KsGet | KsTopology, node, channel), $"MUTE node={node} channel={channel}");
            }
        }
    }

    private static byte[] PropertyRequest(Guid set, uint id, uint flags, int? node, int? channel)
    {
        int length = channel.HasValue ? 40 : node.HasValue ? 32 : 24;
        byte[] data = new byte[length];
        set.ToByteArray().CopyTo(data, 0);
        BitConverter.GetBytes(id).CopyTo(data, 16);
        BitConverter.GetBytes(flags).CopyTo(data, 20);
        if (node.HasValue) BitConverter.GetBytes(node.Value).CopyTo(data, 24);
        if (channel.HasValue) BitConverter.GetBytes(channel.Value).CopyTo(data, 32);
        return data;
    }

    private static byte[] Ioctl(SafeFileHandle handle, byte[] request, int capacity, string label)
    {
        byte[] output = new byte[capacity];
        if (!DeviceIoControl(handle, IoctlKsProperty, request, request.Length, output, output.Length, out uint got, IntPtr.Zero))
        {
            Console.WriteLine($"{label} ERROR {Marshal.GetLastWin32Error()}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return [];
        }
        return output.AsSpan(0, (int)got).ToArray();
    }

    private static int? TryIoctlInt(SafeFileHandle handle, byte[] request, string label)
    {
        byte[] result = Ioctl(handle, request, 16, label);
        if (result.Length < 4) return null;
        int value = BitConverter.ToInt32(result, 0);
        Console.WriteLine($"    {label} value={value} (0x{value:X8}) bytes={result.Length}");
        return value;
    }

    private static void RoundTrip(SafeFileHandle handle, int node, int[] original)
    {
        int target = original.Min() <= -16256 ? original.Min() + 128 : original.Min() - 128;
        Console.WriteLine($"ROUNDTRIP original=[{string.Join(',', original)}] target={target}");
        try
        {
            for (int channel = 0; channel < 2; channel++) SetInt(handle, PropertyRequest(KsPropSetAudio, 4, KsSet | KsTopology, node, channel), target, $"SET channel={channel}");
            for (int channel = 0; channel < 2; channel++) _ = TryIoctlInt(handle, PropertyRequest(KsPropSetAudio, 4, KsGet | KsTopology, node, channel), $"VERIFY_SET channel={channel}");
            Console.WriteLine("ROUNDTRIP_HOLD 5 seconds");
            Thread.Sleep(5000);
        }
        finally
        {
            for (int channel = 0; channel < 2; channel++) SetInt(handle, PropertyRequest(KsPropSetAudio, 4, KsSet | KsTopology, node, channel), original[channel], $"RESTORE channel={channel}");
            for (int channel = 0; channel < 2; channel++) _ = TryIoctlInt(handle, PropertyRequest(KsPropSetAudio, 4, KsGet | KsTopology, node, channel), $"VERIFY_RESTORE channel={channel}");
        }
    }

    private static void SetInt(SafeFileHandle handle, byte[] request, int value, string label)
    {
        byte[] data = BitConverter.GetBytes(value);
        if (!DeviceIoControl(handle, IoctlKsProperty, request, request.Length, data, data.Length, out uint got, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), label);
        Console.WriteLine($"    {label} value={value} returned={got}");
    }

    private static void TryIoctlHex(SafeFileHandle handle, byte[] request, string label)
    {
        byte[] result = Ioctl(handle, request, 256, label);
        if (result.Length > 0) Console.WriteLine($"    {label} bytes={result.Length} hex={Convert.ToHexString(result)}");
    }

    private static string GetPath(IntPtr set, ref SpDeviceInterfaceData iface)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
        IntPtr detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally { Marshal.FreeHGlobal(detail); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SpDeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid classGuid, uint index, ref SpDeviceInterfaceData iface);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData iface, IntPtr detail, uint detailSize, out uint required, IntPtr data);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, int inputSize, [In, Out] byte[] output, int outputSize, out uint returned, IntPtr overlapped);
}
