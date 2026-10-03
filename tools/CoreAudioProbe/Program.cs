using System.Runtime.InteropServices;

internal static class Program
{
    private static readonly Guid AudioEndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey FriendlyNameKey = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private const uint DeviceStateActive = 0x1;
    private const uint StgmRead = 0;

    public static int Main(string[] args)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out IMMDeviceCollection collection));
        Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
        for (uint i = 0; i < count; i++)
        {
            Marshal.ThrowExceptionForHR(collection.Item(i, out IMMDevice device));
            Marshal.ThrowExceptionForHR(device.GetId(out string id));
            string name = GetStringProperty(device, FriendlyNameKey);
            Console.WriteLine($"ENDPOINT {name}");
            Console.WriteLine($"  ID {id}");
            if (!name.Contains("Shanling UA4", StringComparison.OrdinalIgnoreCase)) continue;

            Guid iid = AudioEndpointVolumeIid;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out object endpointObject));
            var volume = (IAudioEndpointVolume)endpointObject;
            if (args.Contains("--set-max", StringComparer.OrdinalIgnoreCase))
            {
                Guid context = new("DD27864C-51C2-4FA5-9171-00A8A5C939D7");
                Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(1.0f, ref context));
                Console.WriteLine("  ACTION Windows endpoint master restored to 100%");
            }
            Marshal.ThrowExceptionForHR(volume.GetChannelCount(out uint channels));
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevel(out float db));
            Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out float scalar));
            Marshal.ThrowExceptionForHR(volume.GetMute(out bool muted));
            Marshal.ThrowExceptionForHR(volume.GetVolumeStepInfo(out uint step, out uint stepCount));
            Marshal.ThrowExceptionForHR(volume.QueryHardwareSupport(out uint hardwareSupport));
            Marshal.ThrowExceptionForHR(volume.GetVolumeRange(out float minDb, out float maxDb, out float incrementDb));
            Console.WriteLine($"  VOLUME channels={channels} dB={db:R} scalar={scalar:R} muted={muted} step={step}/{stepCount} range={minDb:R}..{maxDb:R} increment={incrementDb:R}");
            Console.WriteLine($"  HARDWARE_SUPPORT mask=0x{hardwareSupport:X8} volume={(hardwareSupport & 1) != 0} mute={(hardwareSupport & 2) != 0} meter={(hardwareSupport & 4) != 0}");
            for (uint channel = 0; channel < channels; channel++)
            {
                Marshal.ThrowExceptionForHR(volume.GetChannelVolumeLevel(channel, out float channelDb));
                Marshal.ThrowExceptionForHR(volume.GetChannelVolumeLevelScalar(channel, out float channelScalar));
                Console.WriteLine($"  CHANNEL {channel} dB={channelDb:R} scalar={channelScalar:R}");
            }
        }
        return 0;
    }

    private static string GetStringProperty(IMMDevice device, PropertyKey key)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(StgmRead, out IPropertyStore store));
        PropVariant value = default;
        try
        {
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            return value.VariantType == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) ?? "" : $"(VT={value.VariantType})";
        }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

}

internal enum EDataFlow { Render, Capture, All }
internal enum ERole { Console, Multimedia, Communications }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint deviceNumber, out IMMDevice device);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channelNumber, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channelNumber, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
}
