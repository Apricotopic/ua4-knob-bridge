using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static readonly Guid DeviceControlCategory = new("50894FA4-9206-4A91-8728-CF6F268F271D");
    private static readonly Guid UsbAudioControlPropertySet = new("F6D7C04D-C5C3-4181-9ED7-504ABE0CDDFF");
    private const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint IoctlKsProperty = 0x002F0003, KsGet = 1, KsSet = 2;
    private const byte UrbFunctionVendorOther = 0x20;

    private static int Main(string[] args)
    {
        string[] paths = [.. EnumerateDeviceControlPaths()];
        Console.WriteLine($"device-control interfaces found: {paths.Length}");
        foreach (string candidate in paths) Console.WriteLine(candidate);
        string path = paths.FirstOrDefault(path => path.Contains("vid_20b1&pid_3026", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Shanling UA4 device-control interface not found.");
        using SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite, ShareRead | ShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open UA4 device-control interface");

        Console.WriteLine(path);
        int? setVolume = args.Length == 2 && args[0].Equals("--set", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(args[1]) : null;
        if (setVolume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(args), "Volume must be 0 through 100.");
        if (setVolume is int target)
        {
            Console.WriteLine($"setting hardware volume={target}");
            SubmitOut(handle, 0xA0, [0xC7, 0xA3, 0x01, (byte)target, 0, 0, 0]);
            Thread.Sleep(10);
            (byte[] acknowledgement, uint acknowledgementBytes) = SubmitIn(handle, 0xA1, 12);
            Console.WriteLine($"set response ({acknowledgementBytes} bytes): {Convert.ToHexString(acknowledgement)}");
        }

        Thread.Sleep(10);
        (byte[] response, uint returned) = ReadHardwareState(handle);
        Console.WriteLine($"state ({returned} bytes): {Convert.ToHexString(response)}");
        Console.WriteLine($"reported hardware volume={response[1]}");
        return 0;
    }

    private static (byte[] response, uint returned) ReadHardwareState(SafeFileHandle handle)
    {
        SubmitOut(handle, 0xA0, [0xC7, 0xA3, 0xF0, 0, 0, 0, 0]);
        Thread.Sleep(10);
        return SubmitIn(handle, 0xA1, 12);
    }

    private static void SubmitOut(SafeFileHandle handle, byte request, byte[] payload)
    {
        byte[] property = PropertyRequest(KsSet, request, payload.Length);
        payload.CopyTo(property, 40);
        if (!DeviceIoControl(handle, IoctlKsProperty, property, property.Length, Array.Empty<byte>(), 0, out _, nint.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UA4 vendor OUT transfer");
    }

    private static (byte[] response, uint returned) SubmitIn(SafeFileHandle handle, byte request, int length)
    {
        byte[] property = PropertyRequest(KsGet, request, length);
        byte[] response = new byte[length];
        if (!DeviceIoControl(handle, IoctlKsProperty, property, property.Length, response, response.Length, out uint returned, nint.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UA4 vendor IN transfer");
        return (response, returned);
    }

    private static byte[] PropertyRequest(uint flags, byte request, int length)
    {
        byte[] property = new byte[length + 47];
        UsbAudioControlPropertySet.ToByteArray().CopyTo(property, 0);
        BitConverter.GetBytes(7).CopyTo(property, 16);
        BitConverter.GetBytes(flags).CopyTo(property, 20);
        property[24] = UrbFunctionVendorOther;
        property[25] = request;
        BitConverter.GetBytes((ushort)0).CopyTo(property, 26);
        BitConverter.GetBytes((ushort)0x09A0).CopyTo(property, 28);
        BitConverter.GetBytes(length).CopyTo(property, 36);
        return property;
    }

    private static IEnumerable<string> EnumerateDeviceControlPaths()
    {
        Guid category = DeviceControlCategory;
        nint set = SetupDiGetClassDevs(ref category, null, nint.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new nint(-1)) yield break;
        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInterface = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, nint.Zero, ref category, index, ref deviceInterface))
                {
                    if (Marshal.GetLastWin32Error() == 259) yield break;
                    continue;
                }
                SetupDiGetDeviceInterfaceDetail(set, ref deviceInterface, nint.Zero, 0, out uint required, nint.Zero);
                nint detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref deviceInterface, detail, required, out _, nint.Zero))
                        yield return Marshal.PtrToStringUni(detail + 4) ?? "";
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SpDeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint data, ref Guid classGuid, uint index, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SpDeviceInterfaceData deviceInterfaceData, nint detail, uint detailSize, out uint requiredSize, nint deviceInfoData);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, int inputSize, [In, Out] byte[] output, int outputSize, out uint returned, nint overlapped);
}
