using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using static Protocol;

if (args.Length == 0)
    Usage();

string verb = args[0].ToLowerInvariant();
var options = ParseOptions(args.Skip(1).ToArray());

switch (verb)
{
    case "backup":
        Backup(Required(options, "--out"));
        break;
    case "plan":
        BuildPlan(Required(options, "--backup"), Required(options, "--out"));
        break;
    case "apply":
        Apply(Required(options, "--plan"), Required(options, "--confirm"));
        break;
    case "verify":
        Verify(Required(options, "--plan"));
        break;
    case "rollback":
        Rollback(Required(options, "--plan"), Required(options, "--confirm"));
        break;
    case "probe-pgdn-write":
        ProbePgDnWrite(Required(options, "--backup"));
        break;
    default:
        Usage();
        break;
}

void Backup(string destination)
{
    using ExactM75Device device = ExactM75Device.Open();
    Dictionary<int, string> tables = [];
    Dictionary<int, string> hashes = [];
    for (int selector = 0; selector < 8; selector++)
    {
        byte[] table = device.ReadTable(selector);
        tables[selector] = Convert.ToHexString(table);
        hashes[selector] = Sha256(table);
    }

    var backup = new BackupFile(
        "RK-M75-action-tables-v1", RequiredVid, RequiredPid, device.Path,
        ReportId, FeatureLength, ReadCommand, WriteCommand, TableLength,
        DateTimeOffset.UtcNow, tables, hashes);
    WriteJson(destination, backup);
    Console.WriteLine($"BACKUP {Path.GetFullPath(destination)}");
    Console.WriteLine($"SHA256 {Sha256(File.ReadAllBytes(destination))}");
}

void BuildPlan(string backupPath, string destination)
{
    BackupFile backup = ReadJson<BackupFile>(backupPath);
    ValidateBackup(backup);
    Dictionary<int, byte[]> baseline = DecodeTables(backup.TablesHex);
    Dictionary<int, byte[]> candidate = baseline.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

    // Device-table dwords are little-endian. Keyboard actions encode the USB
    // usage in the most-significant byte; Consumer actions encode type 0x02 in
    // the least-significant byte and the 16-bit Consumer usage above it.
    foreach (int selector in new[] { 0, 4 })
    {
        PatchAction(candidate[selector], 93, 0x4D000000);   // End
        PatchAction(candidate[selector], 123, 0xFE020002);  // Consumer 0x02FE
        PatchAction(candidate[selector], 125, 0xFF020002);  // Consumer 0x02FF
    }
    foreach (int selector in new[] { 1, 5 })
        PatchAction(candidate[selector], 93, 0x4E000000);   // Page Down

    int[] selectors = [0, 1, 4, 5];
    List<Transaction> apply = BuildTransactions(candidate, selectors, "apply");
    List<Transaction> rollback = BuildTransactions(baseline, selectors, "rollback");
    var plan = new PlanFile(
        "RK-M75-action-write-plan-v1", RequiredVid, RequiredPid, ReportId,
        FeatureLength, WriteCommand, ReadCommand, TableLength,
        Path.GetFullPath(backupPath), Sha256(File.ReadAllBytes(backupPath)),
        selectors,
        baseline.ToDictionary(pair => pair.Key, pair => Sha256(pair.Value)),
        candidate.ToDictionary(pair => pair.Key, pair => Sha256(pair.Value)),
        apply, rollback,
        TransactionToken(apply), TransactionToken(rollback));
    WriteJson(destination, plan);
    Console.WriteLine($"PLAN {Path.GetFullPath(destination)}");
    Console.WriteLine($"PLAN_FILE_SHA256 {Sha256(File.ReadAllBytes(destination))}");
    Console.WriteLine($"APPLY_TOKEN {plan.ApplyToken}");
    Console.WriteLine($"ROLLBACK_TOKEN {plan.RollbackToken}");
}

void Apply(string planPath, string confirmation)
{
    PlanFile plan = LoadAndValidatePlan(planPath);
    if (!FixedEquals(confirmation, plan.ApplyToken))
        throw new InvalidOperationException("refusing apply: --confirm does not match APPLY_TOKEN");

    using ExactM75Device device = ExactM75Device.Open();
    EnsureLiveHashes(device, plan.BaselineTableSha256, plan.Selectors, "baseline");
    try
    {
        device.SendTransactions(plan.ApplyTransactions);
        EnsureLiveHashes(device, plan.CandidateTableSha256, plan.Selectors, "candidate");
        Console.WriteLine("APPLY_OK readback matches candidate");
    }
    catch
    {
        Console.Error.WriteLine("APPLY_FAILED attempting immediate direct-protocol rollback");
        device.SendTransactions(plan.RollbackTransactions);
        EnsureLiveHashes(device, plan.BaselineTableSha256, plan.Selectors, "rollback");
        Console.Error.WriteLine("ROLLBACK_OK baseline restored");
        throw;
    }
}

void Verify(string planPath)
{
    PlanFile plan = LoadAndValidatePlan(planPath);
    using ExactM75Device device = ExactM75Device.Open();
    EnsureLiveHashes(device, plan.CandidateTableSha256, plan.Selectors, "candidate");
    Console.WriteLine("VERIFY_OK readback matches candidate");
}

void Rollback(string planPath, string confirmation)
{
    PlanFile plan = LoadAndValidatePlan(planPath);
    if (!FixedEquals(confirmation, plan.RollbackToken))
        throw new InvalidOperationException("refusing rollback: --confirm does not match ROLLBACK_TOKEN");
    using ExactM75Device device = ExactM75Device.Open();
    device.SendTransactions(plan.RollbackTransactions);
    EnsureLiveHashes(device, plan.BaselineTableSha256, plan.Selectors, "rollback");
    Console.WriteLine("ROLLBACK_OK baseline restored");
}

void ProbePgDnWrite(string backupPath)
{
    BackupFile backup = ReadJson<BackupFile>(backupPath);
    ValidateBackup(backup);
    Dictionary<int, byte[]> baseline = DecodeTables(backup.TablesHex);
    Dictionary<int, byte[]> candidate = baseline.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    PatchAction(candidate[0], 93, 0x4D000000); // M75 PgDn scan -> standard HID End

    string expectedCandidateHash = Sha256(candidate[0]);
    using ExactM75Device device = ExactM75Device.Open();
    EnsureLiveHashes(device, backup.TableSha256, [0], "probe baseline");
    try
    {
        device.SendTransactions(BuildTransactions(candidate, [0], "stage2-pgdn-probe"));
        EnsureLiveHashes(device, new Dictionary<int, string> { [0] = expectedCandidateHash }, [0], "probe candidate");
        Console.WriteLine($"PROBE_WRITE_OK selector=00 scan=93 action=0x4D000000 hash={expectedCandidateHash}");
    }
    finally
    {
        device.SendTransactions(BuildTransactions(baseline, [0], "stage2-pgdn-probe-rollback"));
        EnsureLiveHashes(device, backup.TableSha256, [0], "probe rollback");
        Console.WriteLine("PROBE_ROLLBACK_OK selector=00 baseline restored");
    }
}

PlanFile LoadAndValidatePlan(string path)
{
    PlanFile plan = ReadJson<PlanFile>(path);
    if (plan.Format != "RK-M75-action-write-plan-v1" || plan.Vid != RequiredVid || plan.Pid != RequiredPid ||
        plan.ReportId != ReportId || plan.FeatureLength != FeatureLength || plan.WriteCommand != WriteCommand ||
        plan.ReadCommand != ReadCommand || plan.TableLength != TableLength ||
        !plan.Selectors.SequenceEqual(new[] { 0, 1, 4, 5 }))
        throw new InvalidDataException("plan identity/protocol fields are not the exact M75 values");
    ValidateTransactions(plan.ApplyTransactions, plan.Selectors, "apply");
    ValidateTransactions(plan.RollbackTransactions, plan.Selectors, "rollback");
    if (!FixedEquals(TransactionToken(plan.ApplyTransactions), plan.ApplyToken) ||
        !FixedEquals(TransactionToken(plan.RollbackTransactions), plan.RollbackToken))
        throw new InvalidDataException("plan transaction token mismatch");
    return plan;
}

void ValidateBackup(BackupFile backup)
{
    if (backup.Format != "RK-M75-action-tables-v1" || backup.Vid != RequiredVid || backup.Pid != RequiredPid ||
        backup.ReportId != ReportId || backup.FeatureLength != FeatureLength || backup.ReadCommand != ReadCommand ||
        backup.WriteCommand != WriteCommand || backup.TableLength != TableLength ||
        backup.TablesHex.Count != 8 || Enumerable.Range(0, 8).Any(i => !backup.TablesHex.ContainsKey(i)))
        throw new InvalidDataException("backup identity/protocol fields are not the exact M75 values");
    foreach ((int selector, byte[] table) in DecodeTables(backup.TablesHex))
        if (!backup.TableSha256.TryGetValue(selector, out string? expected) || !FixedEquals(Sha256(table), expected))
            throw new InvalidDataException($"backup selector {selector} hash mismatch");
}

Dictionary<int, byte[]> DecodeTables(Dictionary<int, string> source)
{
    Dictionary<int, byte[]> result = [];
    foreach ((int selector, string hex) in source)
    {
        byte[] table = Convert.FromHexString(hex);
        if (table.Length != TableLength)
            throw new InvalidDataException($"selector {selector} table length {table.Length}");
        result[selector] = table;
    }
    return result;
}

List<Transaction> BuildTransactions(Dictionary<int, byte[]> tables, int[] selectors, string purpose)
{
    List<Transaction> result = [];
    foreach (int selector in selectors)
    {
        byte[] table = tables[selector];
        for (int packet = 1; packet <= PacketCount; packet++)
        {
            int offset = (packet - 1) * PayloadLength;
            int length = Math.Min(PayloadLength, table.Length - offset);
            byte[] report = MakeReport(WriteCommand, selector, PacketCount, packet, table.AsSpan(offset, length));
            result.Add(new Transaction(purpose, selector, packet, PacketCount, length,
                Convert.ToHexString(report), Sha256(report)));
        }
    }
    return result;
}

void ValidateTransactions(List<Transaction> transactions, int[] selectors, string purpose)
{
    if (transactions.Count != selectors.Length * PacketCount)
        throw new InvalidDataException($"{purpose} transaction count mismatch");
    int n = 0;
    foreach (int selector in selectors)
    for (int packet = 1; packet <= PacketCount; packet++)
    {
        Transaction transaction = transactions[n++];
        byte[] report = Convert.FromHexString(transaction.ReportHex);
        int expectedLength = packet == 1 ? 512 : 16;
        if (transaction.Purpose != purpose || transaction.Selector != selector || transaction.Packet != packet ||
            transaction.PacketCount != PacketCount || transaction.PayloadLength != expectedLength ||
            report.Length != FeatureLength || report[0] != ReportId || report[1] != WriteCommand ||
            report[2] != selector || report[3] != 0 || report[4] != PacketCount || report[5] != packet ||
            report[6] != (byte)expectedLength || report[7] != (byte)(expectedLength >> 8) ||
            !FixedEquals(Sha256(report), transaction.ReportSha256))
            throw new InvalidDataException($"invalid {purpose} transaction selector={selector} packet={packet}");
    }
}

void EnsureLiveHashes(ExactM75Device device, Dictionary<int, string> expected, int[] selectors, string label)
{
    foreach (int selector in selectors)
    {
        string actual = Sha256(device.ReadTable(selector));
        if (!expected.TryGetValue(selector, out string? wanted) || !FixedEquals(actual, wanted))
            throw new InvalidOperationException($"{label} selector {selector} mismatch: expected={wanted} actual={actual}");
    }
}

void PatchAction(byte[] table, int scanIndex, uint action)
{
    if ((uint)scanIndex >= 132) throw new ArgumentOutOfRangeException(nameof(scanIndex));
    BitConverter.TryWriteBytes(table.AsSpan(scanIndex * 4, 4), action);
}

string TransactionToken(List<Transaction> transactions)
{
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (Transaction transaction in transactions)
        hash.AppendData(Convert.FromHexString(transaction.ReportHex));
    return Convert.ToHexString(hash.GetHashAndReset());
}

Dictionary<string, string> ParseOptions(string[] values)
{
    if (values.Length % 2 != 0) Usage();
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < values.Length; i += 2)
        result.Add(values[i], values[i + 1]);
    return result;
}

string Required(Dictionary<string, string> values, string name) =>
    values.TryGetValue(name, out string? value) ? value : throw new ArgumentException($"missing {name}");

void Usage()
{
    Console.Error.WriteLine("RkM75ConfigWriter backup --out backup.json");
    Console.Error.WriteLine("RkM75ConfigWriter plan --backup backup.json --out plan.json");
    Console.Error.WriteLine("RkM75ConfigWriter apply --plan plan.json --confirm APPLY_TOKEN");
    Console.Error.WriteLine("RkM75ConfigWriter verify --plan plan.json");
    Console.Error.WriteLine("RkM75ConfigWriter rollback --plan plan.json --confirm ROLLBACK_TOKEN");
    Environment.Exit(2);
}

T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions())
    ?? throw new InvalidDataException($"cannot deserialize {path}");
void WriteJson<T>(string path, T value)
{
    string full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, JsonSerializer.Serialize(value, JsonOptions()));
}
JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
bool FixedEquals(string left, string right)
{
    try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); }
    catch (FormatException) { return false; }
}

sealed class ExactM75Device : IDisposable
{
    const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    readonly SafeFileHandle handle;
    public string Path { get; }

    ExactM75Device(string path, SafeFileHandle value) { Path = path; handle = value; }

    public static ExactM75Device Open()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        nint set = SetupDiGetClassDevs(ref hidGuid, null, nint.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        List<(string Path, SafeFileHandle Handle)> matches = [];
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
                SetupDiGetDeviceInterfaceDetail(set, ref iface, nint.Zero, 0, out uint needed, nint.Zero);
                nint detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, nint.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (!path.Contains("vid_258a&pid_0163&mi_01&col05", StringComparison.OrdinalIgnoreCase))
                        continue;
                    SafeFileHandle candidate = CreateFile(path, 0, ShareRead | ShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);
                    if (candidate.IsInvalid) { candidate.Dispose(); continue; }
                    var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
                    if (!HidD_GetAttributes(candidate, ref attributes) ||
                        attributes.VendorId != RequiredVid || attributes.ProductId != RequiredPid ||
                        !HidD_GetPreparsedData(candidate, out nint preparsed))
                    { candidate.Dispose(); continue; }
                    try
                    {
                        if (HidP_GetCaps(preparsed, out HidpCaps caps) < 0 || caps.UsagePage != RequiredUsagePage ||
                            caps.Usage != RequiredUsage || caps.FeatureReportByteLength != FeatureLength)
                        { candidate.Dispose(); continue; }
                    }
                    finally { HidD_FreePreparsedData(preparsed); }
                    matches.Add((path, candidate));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        if (matches.Count != 1)
        {
            foreach ((_, SafeFileHandle item) in matches) item.Dispose();
            throw new InvalidOperationException($"refusing: expected exactly one RK M75 configuration interface, found {matches.Count}");
        }
        return new ExactM75Device(matches[0].Path, matches[0].Handle);
    }

    public byte[] ReadTable(int selector)
    {
        byte[] table = new byte[TableLength];
        for (int packet = 1; packet <= PacketCount; packet++)
        {
            int offset = (packet - 1) * PayloadLength;
            int length = Math.Min(PayloadLength, TableLength - offset);
            byte[] request = MakeReport(ReadCommand, selector, PacketCount, packet, ReadOnlySpan<byte>.Empty);
            request[6] = (byte)length;
            request[7] = (byte)(length >> 8);
            bool complete = false;
            for (int attempt = 1; attempt <= MaxAttempts && !complete; attempt++)
            {
                if (!HidD_SetFeature(handle, request, request.Length))
                {
                    if (attempt == MaxAttempts) throw new Win32Exception(Marshal.GetLastWin32Error(), "read request SetFeature failed");
                    Thread.Sleep(RetryDelayMs);
                    continue;
                }
                Thread.Sleep(PacketDelayMs);
                byte[] response = request.ToArray();
                if (!HidD_GetFeature(handle, response, response.Length))
                {
                    if (attempt == MaxAttempts) throw new Win32Exception(Marshal.GetLastWin32Error(), "read response GetFeature failed");
                    Thread.Sleep(RetryDelayMs);
                    continue;
                }
                if (response[0] != ReportId || response[1] != ReadCommand || response[2] != selector ||
                    response[4] != PacketCount || response[5] != packet || response[6] != (byte)length || response[7] != (byte)(length >> 8))
                    throw new InvalidDataException($"invalid readback header selector={selector} packet={packet}");
                response.AsSpan(8, length).CopyTo(table.AsSpan(offset, length));
                complete = true;
            }
        }
        return table;
    }

    public void SendTransactions(List<Transaction> transactions)
    {
        int priorSelector = -1;
        foreach (Transaction transaction in transactions)
        {
            if (transaction.Selector != priorSelector)
            {
                Thread.Sleep(LayerDelayMs);
                priorSelector = transaction.Selector;
            }
            byte[] report = Convert.FromHexString(transaction.ReportHex);
            Thread.Sleep(PacketDelayMs);
            bool sent = false;
            int lastError = 0;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                sent = HidD_SetFeature(handle, report, report.Length);
                lastError = Marshal.GetLastWin32Error();
                if (sent) break;
                if (attempt < MaxAttempts) Thread.Sleep(RetryDelayMs);
            }
            if (!sent) throw new Win32Exception(lastError,
                $"write SetFeature failed selector={transaction.Selector} packet={transaction.Packet}");
        }
    }

    public void Dispose() => handle.Dispose();

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
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
    [StructLayout(LayoutKind.Sequential)] struct HidAttributes { public int Size; public ushort VendorId, ProductId, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] unsafe struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
}

sealed record BackupFile(string Format, int Vid, int Pid, string DevicePath, int ReportId, int FeatureLength,
    int ReadCommand, int WriteCommand, int TableLength, DateTimeOffset CreatedUtc,
    Dictionary<int, string> TablesHex, Dictionary<int, string> TableSha256);
sealed record Transaction(string Purpose, int Selector, int Packet, int PacketCount, int PayloadLength,
    string ReportHex, string ReportSha256);
sealed record PlanFile(string Format, int Vid, int Pid, int ReportId, int FeatureLength,
    int WriteCommand, int ReadCommand, int TableLength, string BackupPath, string BackupFileSha256,
    int[] Selectors, Dictionary<int, string> BaselineTableSha256, Dictionary<int, string> CandidateTableSha256,
    List<Transaction> ApplyTransactions, List<Transaction> RollbackTransactions,
    string ApplyToken, string RollbackToken);

static class Protocol
{
    public const ushort RequiredVid = 0x258A;
    public const ushort RequiredPid = 0x0163;
    public const ushort RequiredUsagePage = 0xFF02;
    public const ushort RequiredUsage = 0x0001;
    public const int FeatureLength = 520;
    public const byte ReportId = 0x09;
    public const byte WriteCommand = 0x03;
    public const byte ReadCommand = 0x83;
    public const int TableLength = 132 * 4;
    public const int PayloadLength = 512;
    public const int PacketCount = 2;
    public const int PacketDelayMs = 25;
    public const int LayerDelayMs = 30;
    public const int RetryDelayMs = 50;
    public const int MaxAttempts = 3;

    public static byte[] MakeReport(byte command, int selector, int packetCount, int packet, ReadOnlySpan<byte> payload)
    {
        if ((uint)selector > 7 || packetCount != PacketCount || packet is < 1 or > PacketCount || payload.Length > PayloadLength)
            throw new ArgumentOutOfRangeException();
        byte[] report = new byte[FeatureLength];
        report[0] = ReportId;
        report[1] = command;
        report[2] = checked((byte)selector);
        report[4] = checked((byte)packetCount);
        report[5] = checked((byte)packet);
        report[6] = (byte)(payload.Length & 0xFF);
        report[7] = checked((byte)(payload.Length >> 8));
        payload.CopyTo(report.AsSpan(8));
        return report;
    }
}
