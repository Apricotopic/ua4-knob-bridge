using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ua4KnobBridge;

/// <summary>
/// Controls the UA4's own displayed volume through the CTUAC2 driver's vendor
/// control interface. This does not use Core Audio or change Windows master volume.
/// </summary>
public sealed class Ua4VendorVolumeController : IUa4VolumeController
{
    private static readonly Guid DeviceControlCategory = new("50894FA4-9206-4A91-8728-CF6F268F271D");
    private static readonly Guid UsbAudioControlPropertySet = new("F6D7C04D-C5C3-4181-9ED7-504ABE0CDDFF");
    private const uint DigcfPresent = 2, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint IoctlKsProperty = 0x002F0003, KsGet = 1, KsSet = 2;
    private const byte UrbFunctionVendorOther = 0x20, RequestOut = 0xA0, RequestIn = 0xA1;
    private readonly object gate = new();
    private SafeFileHandle? handle;

    public bool IsConnected { get; private set; }
    public int? CurrentVolume { get; private set; }
    public string? DevicePath { get; private set; }

    public void Refresh()
    {
        lock (gate)
        {
            try
            {
                EnsureOpen();
                CurrentVolume = ReadVolumeCore();
                IsConnected = true;
            }
            catch
            {
                Disconnect();
            }
        }
    }

    public int ReadVolume()
    {
        lock (gate)
        {
            EnsureOpen();
            int volume = ReadVolumeCore();
            CurrentVolume = volume;
            return volume;
        }
    }

    public int ChangeVolume(int delta) => ChangeVolume(delta, null);

    public int ChangeVolume(int delta, Action<int>? volumeSubmitted)
    {
        lock (gate)
        {
            EnsureOpen();
            // Refresh establishes the hardware state at connection and keeps it
            // synchronized for UA4-side changes. Re-reading it for every encoder
            // detent inserted a complete vendor round-trip before the actual
            // volume write, which was visible as screen latency.
            int current = CurrentVolume ?? ReadVolumeCore();
            int target = Math.Clamp(current + delta, 0, 100);
            if (current == target) return current;

            // Submit the device-side volume write first. This is the point at
            // which the UA4 updates its physical display, so do not wait for a
            // state query or verification read before it.
            SubmitOut([0xC7, 0xA3, 0x01, (byte)target, 0, 0, 0]);
            CurrentVolume = target;
            volumeSubmitted?.Invoke(target);

            // The UA4 needs a short device commit interval before its command
            // acknowledgement becomes valid. This is its only required delay;
            // the previous pre-write state query and post-write verification
            // are intentionally off the latency path.
            Thread.Sleep(10);
            AcknowledgeSetCore();
            return target;
        }
    }

    public int SetVolume(int volume)
    {
        lock (gate)
        {
            EnsureOpen();
            int current = ReadVolumeCore();
            int verified = SetVolumeCore(Math.Clamp(volume, 0, 100), current);
            CurrentVolume = verified;
            return verified;
        }
    }

    private int ReadVolumeCore()
    {
        SubmitOut([0xC7, 0xA3, 0xF0, 0, 0, 0, 0]);
        Thread.Sleep(10);
        byte[] state = SubmitIn(12);
        if (state.Length != 12 || state[0] != 0)
            throw new IOException($"Unexpected UA4 state response: {Convert.ToHexString(state)}");
        return state[1];
    }

    private int SetVolumeCore(int target, int current, Action<int>? volumeSubmitted = null)
    {
        if (current == target) return current;

        // Send exactly one absolute device-level change. Never retry a set command:
        // if a verification read fails, a later knob event reads current hardware state
        // first, avoiding an accidental duplicate volume step.
        SubmitOut([0xC7, 0xA3, 0x01, (byte)target, 0, 0, 0]);
        // DeviceIoControl has accepted the vendor write. Notify the UI now so its
        // number tracks the UA4 screen while the worker performs verification.
        volumeSubmitted?.Invoke(target);
        Thread.Sleep(10);
        _ = SubmitIn(12); // Command acknowledgement; the full state below is authoritative.
        Thread.Sleep(10);
        int verified = ReadVolumeCore();
        if (verified != target)
            throw new IOException($"UA4 device volume verification failed: requested {target}, read {verified}.");
        return verified;
    }

    private void AcknowledgeSetCore()
    {
        byte[] acknowledgement = SubmitIn(12);
        // CTUAC2 returns either an echoed or zero-prefixed acknowledgement
        // depending on when the device commits the write. Both are valid;
        // only the fixed reply size is stable across firmware timing.
        if (acknowledgement.Length != 12)
            throw new IOException($"Unexpected UA4 set acknowledgement: {Convert.ToHexString(acknowledgement)}");
    }

    private void EnsureOpen()
    {
        if (handle is { IsInvalid: false, IsClosed: false }) return;

        Disconnect();
        string path = EnumerateDeviceControlPaths().FirstOrDefault(IsUa4ControlPath)
            ?? throw new IOException("Shanling UA4 vendor-control interface is not available.");
        SafeFileHandle candidate = CreateFile(path, GenericRead | GenericWrite, ShareRead | ShareWrite, nint.Zero, OpenExisting, 0, nint.Zero);
        if (candidate.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            candidate.Dispose();
            throw new Win32Exception(error, "Open UA4 vendor-control interface");
        }

        handle = candidate;
        DevicePath = path;
        IsConnected = true;
    }

    private void SubmitOut(byte[] payload)
    {
        byte[] property = PropertyRequest(KsSet, RequestOut, payload.Length);
        payload.CopyTo(property, 40);
        if (!DeviceIoControl(handle!, IoctlKsProperty, property, property.Length, Array.Empty<byte>(), 0, out _, nint.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UA4 vendor OUT transfer");
    }

    private byte[] SubmitIn(int length)
    {
        byte[] property = PropertyRequest(KsGet, RequestIn, length);
        byte[] response = new byte[length];
        if (!DeviceIoControl(handle!, IoctlKsProperty, property, property.Length, response, response.Length, out uint returned, nint.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UA4 vendor IN transfer");
        return response.AsSpan(0, checked((int)returned)).ToArray();
    }

    private static byte[] PropertyRequest(uint flags, byte request, int length)
    {
        // CTUAC2 consumes the vendor payload inline at byte 40 of this KS property buffer.
        byte[] property = new byte[length + 47];
        UsbAudioControlPropertySet.ToByteArray().CopyTo(property, 0);
        BitConverter.GetBytes(7).CopyTo(property, 16);
        BitConverter.GetBytes(flags).CopyTo(property, 20);
        property[24] = UrbFunctionVendorOther;
        property[25] = request;
        BitConverter.GetBytes((ushort)0x09A0).CopyTo(property, 28);
        BitConverter.GetBytes(length).CopyTo(property, 36);
        return property;
    }

    private static bool IsUa4ControlPath(string path) =>
        path.Contains("vid_20b1&pid_3026&mi_00", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith("\\control", StringComparison.OrdinalIgnoreCase);

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
                nint detail = Marshal.AllocHGlobal(checked((int)required));
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

    private void Disconnect()
    {
        handle?.Dispose();
        handle = null;
        IsConnected = false;
        CurrentVolume = null;
        DevicePath = null;
    }

    public void Dispose()
    {
        lock (gate) Disconnect();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public uint Flags; public nint Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint data, ref Guid classGuid, uint index, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref SpDeviceInterfaceData deviceInterfaceData, nint detail, uint detailSize, out uint requiredSize, nint deviceInfoData);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, int inputSize, [In, Out] byte[] output, int outputSize, out uint returned, nint overlapped);
}
