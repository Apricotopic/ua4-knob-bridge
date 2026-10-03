using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlHidGetReportDescriptor = 0x000B0007;

    public static int Main(string[] args)
    {
        string[] filters = args.Length == 0 ? ["vid_258a&pid_0163", "vid_20b1&pid_3026"] : args;
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref iface))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }

                SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (!filters.Any(f => f.Equals("all", StringComparison.OrdinalIgnoreCase) || path.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;
                    Probe(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return 0;
    }

    private static void Probe(string path)
    {
        Console.WriteLine($"PATH {path}");
        using SafeFileHandle handle = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            Console.WriteLine($"  OPEN ERROR {Marshal.GetLastWin32Error()}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return;
        }

        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (HidD_GetAttributes(handle, ref attributes))
            Console.WriteLine($"  ATTR VID={attributes.VendorId:X4} PID={attributes.ProductId:X4} VERSION={attributes.VersionNumber:X4}");
        Console.WriteLine($"  STRINGS manufacturer={GetString(handle, HidD_GetManufacturerString)} product={GetString(handle, HidD_GetProductString)} serial={GetString(handle, HidD_GetSerialNumberString)}");

        if (HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            try
            {
                int status = HidP_GetCaps(preparsed, out HidpCaps caps);
                Console.WriteLine($"  CAPS status=0x{status:X8} usagePage=0x{caps.UsagePage:X4} usage=0x{caps.Usage:X4} inputBytes={caps.InputReportByteLength} outputBytes={caps.OutputReportByteLength} featureBytes={caps.FeatureReportByteLength}");
                Console.WriteLine($"       inputButtons={caps.NumberInputButtonCaps} inputValues={caps.NumberInputValueCaps} outputButtons={caps.NumberOutputButtonCaps} outputValues={caps.NumberOutputValueCaps} featureButtons={caps.NumberFeatureButtonCaps} featureValues={caps.NumberFeatureValueCaps}");
                DumpCaps(preparsed, 0, "INPUT", caps.NumberInputButtonCaps, caps.NumberInputValueCaps);
                DumpCaps(preparsed, 1, "OUTPUT", caps.NumberOutputButtonCaps, caps.NumberOutputValueCaps);
                DumpCaps(preparsed, 2, "FEATURE", caps.NumberFeatureButtonCaps, caps.NumberFeatureValueCaps);
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        else Console.WriteLine($"  PREPARSED ERROR {Marshal.GetLastWin32Error()}");

        byte[] descriptor = new byte[4096];
        if (DeviceIoControl(handle, IoctlHidGetReportDescriptor, IntPtr.Zero, 0, descriptor, descriptor.Length, out uint got, IntPtr.Zero))
            Console.WriteLine($"  REPORT_DESCRIPTOR {got} bytes: {Convert.ToHexString(descriptor, 0, (int)got)}");
        else
            Console.WriteLine($"  REPORT_DESCRIPTOR unavailable via IOCTL (error {Marshal.GetLastWin32Error()})");
        Console.WriteLine();
    }

    private static void DumpCaps(IntPtr preparsed, int reportType, string name, ushort buttonCount, ushort valueCount)
    {
        const int capSize = 72;
        if (buttonCount > 0)
        {
            IntPtr memory = Marshal.AllocHGlobal(capSize * buttonCount);
            try
            {
                ushort count = buttonCount;
                int status = HidP_GetButtonCaps(reportType, memory, ref count, preparsed);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = memory + i * capSize;
                    ushort page = U16(p, 0), bitField = U16(p, 4), link = U16(p, 6), linkUsage = U16(p, 8), linkPage = U16(p, 10);
                    byte reportId = U8(p, 2), alias = U8(p, 3), isRange = U8(p, 12), absolute = U8(p, 15);
                    if (isRange != 0)
                        Console.WriteLine($"    {name} BUTTON[{i}] status=0x{status:X8} reportId=0x{reportId:X2} page=0x{page:X4} usage=0x{U16(p, 56):X4}-0x{U16(p, 58):X4} dataIndex={U16(p, 68)}-{U16(p, 70)} bitField=0x{bitField:X4} link={link}:0x{linkPage:X4}/0x{linkUsage:X4} alias={alias} absolute={absolute}");
                    else
                        Console.WriteLine($"    {name} BUTTON[{i}] status=0x{status:X8} reportId=0x{reportId:X2} page=0x{page:X4} usage=0x{U16(p, 56):X4} dataIndex={U16(p, 64)} bitField=0x{bitField:X4} link={link}:0x{linkPage:X4}/0x{linkUsage:X4} alias={alias} absolute={absolute}");
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        if (valueCount > 0)
        {
            IntPtr memory = Marshal.AllocHGlobal(capSize * valueCount);
            try
            {
                ushort count = valueCount;
                int status = HidP_GetValueCaps(reportType, memory, ref count, preparsed);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = memory + i * capSize;
                    ushort page = U16(p, 0), bitField = U16(p, 4), link = U16(p, 6), linkUsage = U16(p, 8), linkPage = U16(p, 10);
                    byte reportId = U8(p, 2), isRange = U8(p, 12), absolute = U8(p, 15), hasNull = U8(p, 16);
                    ushort bitSize = U16(p, 18), reportCount = U16(p, 20);
                    int logicalMin = I32(p, 40), logicalMax = I32(p, 44), physicalMin = I32(p, 48), physicalMax = I32(p, 52);
                    string usage = isRange != 0 ? $"0x{U16(p, 56):X4}-0x{U16(p, 58):X4}" : $"0x{U16(p, 56):X4}";
                    Console.WriteLine($"    {name} VALUE[{i}] status=0x{status:X8} reportId=0x{reportId:X2} page=0x{page:X4} usage={usage} bits={bitSize} count={reportCount} logical={logicalMin}..{logicalMax} physical={physicalMin}..{physicalMax} bitField=0x{bitField:X4} link={link}:0x{linkPage:X4}/0x{linkUsage:X4} null={hasNull} absolute={absolute}");
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }

    private static byte U8(IntPtr p, int offset) => Marshal.ReadByte(p, offset);
    private static ushort U16(IntPtr p, int offset) => unchecked((ushort)Marshal.ReadInt16(p, offset));
    private static int I32(IntPtr p, int offset) => Marshal.ReadInt32(p, offset);

    private delegate bool HidStringFunction(SafeFileHandle handle, IntPtr buffer, uint bufferLength);
    private static string GetString(SafeFileHandle handle, HidStringFunction fn)
    {
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try { return fn(handle, buffer, 512) ? Marshal.PtrToStringUni(buffer) ?? "" : ""; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HiddAttributes attributes);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetManufacturerString(SafeFileHandle handle, IntPtr buffer, uint bufferLength);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetProductString(SafeFileHandle handle, IntPtr buffer, uint bufferLength);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, IntPtr buffer, uint bufferLength);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int reportType, IntPtr buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);
    [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr input, int inputSize, [Out] byte[] output, int outputSize, out uint bytesReturned, IntPtr overlapped);
}
