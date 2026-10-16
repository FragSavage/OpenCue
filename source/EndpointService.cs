using System.Runtime.InteropServices;

namespace OpenCue;

/// <summary>Read-only enumeration of active Windows playback endpoints.</summary>
public static class EndpointService
{
    private const uint DeviceStateActive = 0x00000001;
    private const int ErrorNotFound = unchecked((int)0x80070490);
    private const ushort VariantWideString = 31; // VT_LPWSTR

    // Functiondiscoverykeys_devpkey.h: PKEY_Device_FriendlyName.
    private static readonly PropertyKey FriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>
    /// Returns active render endpoints, with the current Multimedia default first.
    /// An empty list means Windows reports no active playback devices. Other failures
    /// are reported as exceptions so the UI can distinguish them from an empty list.
    /// This does not activate a stream or change any device, volume or routing setting.
    /// </summary>
    public static IReadOnlyList<RenderEndpoint> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Playback device discovery requires Windows.");

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            var defaultId = ReadDefaultId(enumerator);
            Check(enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceStateActive, out devices),
                "list active playback devices");
            Check(devices.GetCount(out var count), "count active playback devices");

            var result = new List<RenderEndpoint>();
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Check(devices.Item(index, out device), $"read playback device {index + 1}");
                    Check(device.GetId(out var id), $"read the ID of playback device {index + 1}");
                    if (string.IsNullOrWhiteSpace(id))
                        throw new InvalidOperationException($"Playback device {index + 1} has no endpoint ID.");

                    var name = ReadFriendlyName(device, id);
                    result.Add(new RenderEndpoint(id, name,
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    Release(device);
                }
            }

            result.Sort((left, right) =>
            {
                var byDefault = right.IsDefault.CompareTo(left.IsDefault);
                if (byDefault != 0) return byDefault;
                var byName = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            return result.AsReadOnly();
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                $"Windows playback device discovery failed (0x{exception.HResult:X8}). {exception.Message}",
                exception);
        }
        finally
        {
            Release(devices);
            Release(enumerator);
        }
    }

    private static string? ReadDefaultId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? defaultDevice = null;
        try
        {
            var status = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out defaultDevice);
            // No configured default is a valid state, including when the list is empty.
            if (status == ErrorNotFound) return null;
            Check(status, "find the default Multimedia playback device");
            Check(defaultDevice.GetId(out var id), "read the default playback device ID");
            return id;
        }
        finally
        {
            Release(defaultDevice);
        }
    }

    private static string ReadFriendlyName(IMMDevice device, string endpointId)
    {
        IPropertyStore? properties = null;
        var value = default(PropVariant);
        try
        {
            // STGM_READ = 0. No property write or endpoint activation is performed.
            Check(device.OpenPropertyStore(0, out properties), $"open playback properties for {endpointId}");
            var key = FriendlyName;
            Check(properties.GetValue(ref key, out value), $"read the playback name for {endpointId}");
            if (value.VarType != VariantWideString || value.Pointer == IntPtr.Zero)
                throw new InvalidOperationException($"Windows returned no friendly playback name for {endpointId}.");
            var name = Marshal.PtrToStringUni(value.Pointer);
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"Windows returned an empty playback name for {endpointId}.");
            return name;
        }
        finally
        {
            // The property store owns the allocation convention, so always use the
            // system PROPVARIANT cleanup rather than freeing the string separately.
            try
            {
                Check(PropVariantClear(ref value), "release playback property memory");
            }
            finally
            {
                Release(properties);
            }
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"Could not {operation}. Try refreshing the device list after checking Windows Sound settings.", result);
    }

    private static void Release(object? value)
    {
        // One release per locally acquired RCW reference. FinalReleaseComObject would
        // also invalidate another reference when default and enumerated device agree.
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    // Full native storage: 16 bytes on x86 and 24 bytes on x64. The value union
    // starts at offset 8; its largest counted-pointer member needs two pointer slots.
    // https://learn.microsoft.com/windows/win32/api/propidl/ns-propidl-propvariant
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VarType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr ReservedPayload;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    private enum DataFlow { Render, Capture, All }
    private enum Role { Console, Multimedia, Communications }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    // Methods are declared in native vtable order; PreserveSig keeps HRESULT values.
    // https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(in Guid iid, uint classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
