using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static readonly Guid UsbHubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2, OpenExisting = 3;
    private const uint IoctlUsbGetNodeInformation = 0x00220408;
    private const uint IoctlUsbGetDescriptorFromNodeConnection = 0x00220410;
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x00220448;

    public static int Main(string[] args)
    {
        ushort wantedVid = args.Length > 0 ? Convert.ToUInt16(args[0], 16) : (ushort)0x20B1;
        ushort wantedPid = args.Length > 1 ? Convert.ToUInt16(args[1], 16) : (ushort)0x3026;
        Guid hubInterface = UsbHubInterface;
        IntPtr set = SetupDiGetClassDevs(ref hubInterface, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        int matches = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hubInterface, index, ref iface))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                string path = GetInterfacePath(set, ref iface);
                matches += ProbeHub(path, wantedVid, wantedPid);
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        Console.WriteLine($"MATCHES {matches}");
        return matches == 0 ? 2 : 0;
    }

    private static int ProbeHub(string path, ushort wantedVid, ushort wantedPid)
    {
        using SafeFileHandle hub = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (hub.IsInvalid) return 0;
        byte[] node = new byte[128];
        if (!DeviceIoControl(hub, IoctlUsbGetNodeInformation, node, node.Length, node, node.Length, out _, IntPtr.Zero)) return 0;
        int ports = node[6];
        int matches = 0;
        for (int port = 1; port <= ports; port++)
        {
            byte[] connection = new byte[4096];
            BitConverter.GetBytes(port).CopyTo(connection, 0);
            if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationEx, connection, connection.Length, connection, connection.Length, out _, IntPtr.Zero)) continue;
            ushort vid = BitConverter.ToUInt16(connection, 12), pid = BitConverter.ToUInt16(connection, 14);
            if (vid != wantedVid || pid != wantedPid) continue;
            matches++;
            ushort bcdDevice = BitConverter.ToUInt16(connection, 16);
            Console.WriteLine($"DEVICE hub={path} port={port} VID={vid:X4} PID={pid:X4} bcdDevice={bcdDevice:X4}");
            byte[] config = ReadDescriptor(hub, port, 2, 0, 9);
            int total = config.Length >= 4 ? BitConverter.ToUInt16(config, 2) : 0;
            if (total > 9) config = ReadDescriptor(hub, port, 2, 0, total);
            Console.WriteLine($"CONFIG {config.Length} bytes {Convert.ToHexString(config)}");
            ParseDescriptors(config);
            foreach ((int interfaceNumber, int reportLength) in FindHidReports(config))
            {
                byte[] report = ReadDescriptor(hub, port, 0x22, 0, reportLength, interfaceNumber);
                Console.WriteLine($"HID_REPORT interface={interfaceNumber} {report.Length} bytes {Convert.ToHexString(report)}");
            }
        }
        return matches;
    }

    private static byte[] ReadDescriptor(SafeFileHandle hub, int port, byte type, byte index, int length, int descriptorIndex = 0)
    {
        byte[] request = new byte[12 + length];
        BitConverter.GetBytes(port).CopyTo(request, 0);
        request[4] = type == 0x22 ? (byte)0x81 : (byte)0x80;
        request[5] = 0x06;
        BitConverter.GetBytes((ushort)((type << 8) | index)).CopyTo(request, 6);
        BitConverter.GetBytes((ushort)descriptorIndex).CopyTo(request, 8);
        BitConverter.GetBytes((ushort)length).CopyTo(request, 10);
        if (!DeviceIoControl(hub, IoctlUsbGetDescriptorFromNodeConnection, request, request.Length, request, request.Length, out uint got, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GET_DESCRIPTOR type={type} index={index}");
        int dataLength = Math.Max(0, Math.Min(length, (int)got - 12));
        return request.AsSpan(12, dataLength).ToArray();
    }

    private static IEnumerable<(int InterfaceNumber, int ReportLength)> FindHidReports(byte[] data)
    {
        int currentInterface = -1;
        for (int offset = 0; offset + 2 <= data.Length && data[offset] >= 2 && offset + data[offset] <= data.Length; offset += data[offset])
        {
            int length = data[offset];
            if (data[offset + 1] == 0x04 && length >= 9) currentInterface = data[offset + 2];
            if (data[offset + 1] == 0x21 && length >= 9 && currentInterface >= 0 && data[offset + 6] == 0x22)
                yield return (currentInterface, BitConverter.ToUInt16(data, offset + 7));
        }
    }

    private static void ParseDescriptors(byte[] data)
    {
        int currentInterface = -1, currentAlt = -1;
        for (int offset = 0; offset + 2 <= data.Length && data[offset] >= 2 && offset + data[offset] <= data.Length; offset += data[offset])
        {
            byte length = data[offset], type = data[offset + 1];
            string detail = type switch
            {
                0x02 when length >= 9 => $"CONFIG total={BitConverter.ToUInt16(data, offset + 2)} interfaces={data[offset + 4]}",
                0x04 when length >= 9 => InterfaceDetail(data, offset, out currentInterface, out currentAlt),
                0x05 when length >= 7 => $"ENDPOINT if={currentInterface}/{currentAlt} address=0x{data[offset + 2]:X2} attributes=0x{data[offset + 3]:X2} maxPacket={BitConverter.ToUInt16(data, offset + 4)} interval={data[offset + 6]}",
                0x24 when length >= 3 => $"CS_INTERFACE if={currentInterface}/{currentAlt} subtype=0x{data[offset + 2]:X2}",
                0x25 when length >= 3 => $"CS_ENDPOINT if={currentInterface}/{currentAlt} subtype=0x{data[offset + 2]:X2}",
                _ => $"DESCRIPTOR if={currentInterface}/{currentAlt}"
            };
            Console.WriteLine($"  @{offset:X4} len={length} type=0x{type:X2} {detail} hex={Convert.ToHexString(data, offset, length)}");
        }
    }

    private static string InterfaceDetail(byte[] data, int offset, out int number, out int alt)
    {
        number = data[offset + 2]; alt = data[offset + 3];
        return $"INTERFACE number={number} alt={alt} endpoints={data[offset + 4]} class=0x{data[offset + 5]:X2} subclass=0x{data[offset + 6]:X2} protocol=0x{data[offset + 7]:X2}";
    }

    private static string GetInterfacePath(IntPtr set, ref SpDeviceInterfaceData iface)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In, Out] byte[] input, int inputSize, [In, Out] byte[] output, int outputSize, out uint bytesReturned, IntPtr overlapped);
}
