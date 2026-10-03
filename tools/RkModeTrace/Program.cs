using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = GetArgument(args, "--output")
            ?? Path.Combine(Environment.CurrentDirectory, $"rk-mode-trace-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        int seconds = int.TryParse(GetArgument(args, "--seconds"), out int value) ? value : 180;

        ApplicationConfiguration.Initialize();
        using var trace = new TraceWindow(Path.GetFullPath(output), TimeSpan.FromSeconds(seconds));
        Application.Run(trace);
        return 0;
    }

    private static string? GetArgument(string[] args, string name)
    {
        int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class TraceWindow : Form
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const uint RidiDeviceInfo = 0x2000000B;
    private const uint RimTypeKeyboard = 1;
    private const uint RimTypeHid = 2;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint GidcArrival = 1;
    private const uint GidcRemoval = 2;
    private static readonly string[] RkIds = ["vid_258a&pid_0163", "vid_258a&pid_0150"];

    private readonly StreamWriter writer;
    private readonly System.Windows.Forms.Timer timer;
    private readonly DateTime stopAt;
    private readonly Label status;
    private readonly TextBox recent;

    public TraceWindow(string output, TimeSpan duration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        writer = new StreamWriter(output, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        stopAt = DateTime.Now + duration;

        Text = "RK M75 2.4 GHz read-only knob capture";
        ClientSize = new Size(760, 390);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        status = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 72,
            Padding = new Padding(10),
            Text = "Starting read-only trace..."
        };
        recent = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 9f)
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6)
        };
        var stop = new Button { Text = "Stop and save", AutoSize = true };
        stop.Click += (_, _) => Close();
        buttons.Controls.Add(stop);
        Controls.Add(recent);
        Controls.Add(status);
        Controls.Add(buttons);

        Shown += (_, _) => StartTrace(output);
        FormClosed += (_, _) => writer.Dispose();

        timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            TimeSpan remaining = stopAt - DateTime.Now;
            status.Text = $"READ ONLY — no HID feature/output reports are sent.\r\n" +
                          $"With the M75 connected only through its 2.4 GHz receiver, turn one CCW detent, one CW detent, and allow each release. " +
                          $"Auto-stop: {Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds))} s";
            if (remaining <= TimeSpan.Zero) Close();
        };
    }

    private Button MakeButton(string text, string marker)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => Log(marker);
        return button;
    }

    private void StartTrace(string output)
    {
        RawInputDevice[] registrations =
        [
            new(0x01, 0x06, RidevInputSink | RidevDevNotify, Handle), // keyboard
            new(0x01, 0x80, RidevInputSink | RidevDevNotify, Handle), // system control
            new(0x0C, 0x01, RidevInputSink | RidevDevNotify, Handle), // consumer control
            new(0xFF02, 0x01, RidevInputSink | RidevDevNotify, Handle), // RK vendor collection
        ];
        if (!RegisterRawInputDevices(registrations, (uint)registrations.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices");

        Log($"BEGIN output={output}");
        Log("SAFETY read_only=true APIs=RegisterRawInputDevices,GetRawInputData,GetRawInputDeviceInfo");
        Snapshot("initial");
        timer.Start();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
            HandleRawInput(message.LParam);
        else if (message.Msg == WmInputDeviceChange)
        {
            uint transition = unchecked((uint)message.WParam.ToInt64());
            string kind = transition == GidcArrival ? "arrival" : transition == GidcRemoval ? "removal" : $"0x{transition:X}";
            string path = transition == GidcRemoval ? "<handle no longer queryable>" : GetDeviceName(message.LParam);
            Log($"DEVICE_CHANGE kind={kind} handle=0x{message.LParam:X} path={path}");
            Snapshot($"after-{kind}");
        }
        base.WndProc(ref message);
    }

    private void HandleRawInput(nint rawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawInput, RidInput, null, ref size, headerSize) != 0 || size < headerSize + 8)
            return;
        byte[] buffer = new byte[size];
        if (GetRawInputData(rawInput, RidInput, buffer, ref size, headerSize) != size)
            return;
        uint type = BitConverter.ToUInt32(buffer, 0);

        nint device = IntPtr.Size == 8
            ? (nint)BitConverter.ToInt64(buffer, 8)
            : (nint)BitConverter.ToInt32(buffer, 8);
        string path = GetDeviceName(device);
        if (!IsRk(path))
            return;

        if (type == RimTypeKeyboard)
        {
            int offset = (int)headerSize;
            if (buffer.Length < offset + 16)
                return;
            ushort makeCode = BitConverter.ToUInt16(buffer, offset);
            ushort flags = BitConverter.ToUInt16(buffer, offset + 2);
            ushort virtualKey = BitConverter.ToUInt16(buffer, offset + 6);
            uint windowMessage = BitConverter.ToUInt32(buffer, offset + 8);
            Log($"KEYBOARD handle=0x{device:X} make=0x{makeCode:X4} flags=0x{flags:X4} " +
                $"vk=0x{virtualKey:X4} message=0x{windowMessage:X4} path={path}");
            return;
        }
        if (type != RimTypeHid)
            return;

        int hidOffset = (int)headerSize;
        uint reportSize = BitConverter.ToUInt32(buffer, hidOffset);
        uint reportCount = BitConverter.ToUInt32(buffer, hidOffset + 4);
        int reportOffset = hidOffset + 8;
        RawDeviceInfo info = GetDeviceInfo(device);
        for (uint index = 0; index < reportCount; index++)
        {
            int offset = checked(reportOffset + (int)(index * reportSize));
            if (offset + reportSize > buffer.Length)
                break;
            string hex = Convert.ToHexString(buffer, offset, (int)reportSize);
            string decoded = Decode(info.UsagePage, info.Usage, buffer.AsSpan(offset, (int)reportSize));
            Log($"INPUT handle=0x{device:X} vid=0x{info.VendorId:X4} pid=0x{info.ProductId:X4} up=0x{info.UsagePage:X4} usage=0x{info.Usage:X4} " +
                $"size={reportSize} index={index + 1}/{reportCount} report={hex}{decoded}");
        }
    }

    private static string Decode(ushort page, ushort usage, ReadOnlySpan<byte> report)
    {
        if (page == 0x0C && usage == 0x01 && report.Length >= 3 && report[0] == 0x02)
        {
            ushort consumer = (ushort)(report[1] | report[2] << 8);
            return $" consumer=0x{consumer:X4}";
        }
        if (page == 0x01 && usage == 0x06 && report.Length >= 2)
            return $" keyboard_report_id=0x{report[0]:X2}";
        return "";
    }

    private void Snapshot(string reason)
    {
        uint count = 0;
        uint itemSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        if (GetRawInputDeviceList(null, ref count, itemSize) != 0)
        {
            Log($"SNAPSHOT reason={reason} error={Marshal.GetLastWin32Error()}");
            return;
        }
        var list = new RawInputDeviceList[count];
        if (count > 0 && GetRawInputDeviceList(list, ref count, itemSize) == uint.MaxValue)
        {
            Log($"SNAPSHOT reason={reason} error={Marshal.GetLastWin32Error()}");
            return;
        }
        var rk = list.Select(item => (item.Device, Path: GetDeviceName(item.Device), Info: GetDeviceInfo(item.Device)))
            .Where(item => IsRk(item.Path))
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Log($"SNAPSHOT reason={reason} rk_tlc_count={rk.Length}");
        foreach (var item in rk)
            Log($"  TLC handle=0x{item.Device:X} vid=0x{item.Info.VendorId:X4} pid=0x{item.Info.ProductId:X4} up=0x{item.Info.UsagePage:X4} usage=0x{item.Info.Usage:X4} path={item.Path}");
    }

    private void Log(string text)
    {
        string line = $"{DateTimeOffset.Now:O} {text}";
        writer.WriteLine(line);
        recent.AppendText(line + Environment.NewLine);
        if (recent.TextLength > 30000)
            recent.Text = recent.Text[^20000..];
        recent.SelectionStart = recent.TextLength;
        recent.ScrollToCaret();
        Console.WriteLine(line);
    }

    private static bool IsRk(string? path) => path is not null && RkIds.Any(id => path.Contains(id, StringComparison.OrdinalIgnoreCase));

    private static string GetDeviceName(nint device)
    {
        uint chars = 0;
        _ = GetRawInputDeviceInfoName(device, RidiDeviceName, null, ref chars);
        if (chars == 0)
            return "";
        var name = new StringBuilder((int)chars + 1);
        return GetRawInputDeviceInfoName(device, RidiDeviceName, name, ref chars) == uint.MaxValue ? "" : name.ToString();
    }

    private static RawDeviceInfo GetDeviceInfo(nint device)
    {
        var native = new RidDeviceInfo { Size = (uint)Marshal.SizeOf<RidDeviceInfo>() };
        uint bytes = native.Size;
        if (GetRawInputDeviceInfoStruct(device, RidiDeviceInfo, ref native, ref bytes) == uint.MaxValue || native.Type != RimTypeHid)
            return default;
        return new RawDeviceInfo((ushort)native.VendorId, (ushort)native.ProductId, (ushort)native.UsagePage, (ushort)native.Usage);
    }

    private readonly record struct RawDeviceInfo(ushort VendorId, ushort ProductId, ushort UsagePage, ushort Usage);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RawInputDevice(ushort usagePage, ushort usage, uint flags, nint target)
    {
        public readonly ushort UsagePage = usagePage;
        public readonly ushort Usage = usage;
        public readonly uint Flags = flags;
        public readonly nint Target = target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList { public nint Device; public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public uint Type; public uint Size; public nint Device; public nint WParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RidDeviceInfo
    {
        public uint Size;
        public uint Type;
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
        public uint UnionPadding1;
        public uint UnionPadding2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([In, Out] RawInputDeviceList[]? list, ref uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint rawInput, uint command, [Out] byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoName(nint device, uint command, StringBuilder? data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfoStruct(nint device, uint command, ref RidDeviceInfo data, ref uint size);
}
