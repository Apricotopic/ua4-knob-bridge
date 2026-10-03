using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;

string outputDirectory = args.Length == 2 && args[0] == "--backup" ? Path.GetFullPath(args[1]) : "";
bool query82 = args.Contains("--query82", StringComparer.OrdinalIgnoreCase);
int query82Length = 6;
int query82Index = Array.FindIndex(args, value => value.Equals("--query82-length", StringComparison.OrdinalIgnoreCase));
if (query82Index >= 0 && query82Index + 1 < args.Length) query82Length = int.Parse(args[query82Index + 1]);
int queryCommand = 0x82;
int queryCommandIndex = Array.FindIndex(args, value => value.Equals("--query-command", StringComparison.OrdinalIgnoreCase));
if (queryCommandIndex >= 0 && queryCommandIndex + 1 < args.Length) queryCommand = Convert.ToInt32(args[queryCommandIndex + 1], 16);
int queryPacket = 1;
int queryPacketIndex = Array.FindIndex(args, value => value.Equals("--query-packet", StringComparison.OrdinalIgnoreCase));
if (queryPacketIndex >= 0 && queryPacketIndex + 1 < args.Length) queryPacket = int.Parse(args[queryPacketIndex + 1]);
int querySelector = queryCommand == 0x82 ? 1 : 0;
int querySelectorIndex = Array.FindIndex(args, value => value.Equals("--query-selector", StringComparison.OrdinalIgnoreCase));
if (querySelectorIndex >= 0 && querySelectorIndex + 1 < args.Length) querySelector = Convert.ToInt32(args[querySelectorIndex + 1], 16);
int queryPacketCount = 1;
int queryPacketCountIndex = Array.FindIndex(args, value => value.Equals("--query-packet-count", StringComparison.OrdinalIgnoreCase));
if (queryPacketCountIndex >= 0 && queryPacketCountIndex + 1 < args.Length) queryPacketCount = int.Parse(args[queryPacketCountIndex + 1]);
if (outputDirectory.Length != 0) Directory.CreateDirectory(outputDirectory);

HidD_GetHidGuid(out Guid hidGuid);
nint set = SetupDiGetClassDevs(ref hidGuid, null, nint.Zero, DigcfPresent | DigcfDeviceInterface);
if (set == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
try
{
    for (uint index = 0; ; index++)
    {
        var iface = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
        if (!SetupDiEnumDeviceInterfaces(set, nint.Zero, ref hidGuid, index, ref iface))
        {
            if (Marshal.GetLastWin32Error() == 259) break;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        SetupDiGetDeviceInterfaceDetail(set, ref iface, nint.Zero, 0, out uint required, nint.Zero);
        nint detail = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, nint.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            string path = Marshal.PtrToStringUni(detail + 4) ?? "";
            if (!path.Contains("vid_258a&pid_0163&mi_01", StringComparison.OrdinalIgnoreCase)) continue;
            using SafeFileHandle handle = CreateFile(path, 0, ShareRead | ShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);
            if (handle.IsInvalid) continue;
            if (!HidD_GetPreparsedData(handle, out nint preparsed)) continue;
            try
            {
                if (HidP_GetCaps(preparsed, out HidpCaps caps) < 0) continue;
                Console.WriteLine($"PATH {path}");
                Console.WriteLine($"CAPS usagePage=0x{caps.UsagePage:X4} usage=0x{caps.Usage:X4} input={caps.InputReportByteLength} output={caps.OutputReportByteLength} feature={caps.FeatureReportByteLength}");
                if (query82 && caps.FeatureReportByteLength == 520 && path.Contains("col05", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] query = new byte[520];
                    query[0] = 9;
                    query[1] = (byte)queryCommand;
                    query[2] = checked((byte)querySelector);
                    query[4] = checked((byte)queryPacketCount);
                    query[5] = checked((byte)queryPacket);
                    if (query82Length is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(query82Length));
                    query[6] = (byte)query82Length;
                    query[7] = (byte)(query82Length >> 8);
                    bool setOk = HidD_SetFeature(handle, query, query.Length);
                    int setError = Marshal.GetLastWin32Error();
                    Thread.Sleep(25);
                    bool getOk = HidD_GetFeature(handle, query, query.Length);
                    int getError = Marshal.GetLastWin32Error();
                    Console.WriteLine($"QUERY command=0x{queryCommand:X2} selector=0x{querySelector:X2} packets={queryPacketCount} packet={queryPacket} length={query82Length} set={setOk}/error={setError} get={getOk}/error={getError}");
                    Console.WriteLine($"QUERY_HEAD {Convert.ToHexString(query.AsSpan(0, Math.Min(query.Length, 12 + query82Length)))}");
                    continue;
                }
                if (caps.FeatureReportByteLength == 0) continue;
                byte[] report = new byte[caps.FeatureReportByteLength];
                report[0] = path.Contains("col07", StringComparison.OrdinalIgnoreCase) ? (byte)5
                    : path.Contains("col08", StringComparison.OrdinalIgnoreCase) ? (byte)6
                    : path.Contains("col05", StringComparison.OrdinalIgnoreCase) ? (byte)9
                    : (byte)0;
                if (report[0] == 0) continue;
                bool ok = HidD_GetFeature(handle, report, report.Length);
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"GET_FEATURE id=0x{report[0]:X2} length={report.Length} ok={ok} error={error}");
                if (!ok) continue;
                string hash = Convert.ToHexString(SHA256.HashData(report));
                int nonzero = report.Count(value => value != 0);
                Console.WriteLine($"SHA256 {hash} nonzero={nonzero}");
                Console.WriteLine($"HEAD {Convert.ToHexString(report.AsSpan(0, Math.Min(128, report.Length)))}");
                if (outputDirectory.Length != 0)
                {
                    string collection = path.Contains("col07", StringComparison.OrdinalIgnoreCase) ? "col07-report05"
                        : path.Contains("col08", StringComparison.OrdinalIgnoreCase) ? "col08-report06"
                        : "col05-report09";
                    string destination = Path.Combine(outputDirectory, collection + ".bin");
                    File.WriteAllBytes(destination, report);
                    Console.WriteLine($"BACKUP {destination}");
                }
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        finally { Marshal.FreeHGlobal(detail); }
    }
}
finally { SetupDiDestroyDeviceInfoList(set); }

[DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
[DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out nint data);
[DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(nint data);
[DllImport("hid.dll")] static extern int HidP_GetCaps(nint data, out HidpCaps caps);
[DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle handle, [In, Out] byte[] report, int length);
[DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] report, int length);
[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);
[DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(nint set, nint data, ref Guid guid, uint index, ref SpDeviceInterfaceData iface);
[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SpDeviceInterfaceData iface, nint detail, uint size, out uint required, nint data);
[DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(nint set);
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

[StructLayout(LayoutKind.Sequential)] struct SpDeviceInterfaceData { public int Size; public Guid Guid; public uint Flags; public nint Reserved; }
[StructLayout(LayoutKind.Sequential)] unsafe struct HidpCaps
{
    public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    public fixed ushort Reserved[17];
    public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
    public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
    public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
}
