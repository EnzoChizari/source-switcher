using System.Runtime.InteropServices;
using NAudio.Wave;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

/// <summary>
/// Read-only endpoint queries with one deterministic release per native lease.
/// In particular, property stores must not remain attached to short-lived MMDevice
/// wrappers until a managed collection: the controller reads these on every census.
/// </summary>
internal static class OwnedEndpointAccess
{
    private const int Render = 0;
    private const int Multimedia = 1;
    private const int Active = 1;
    private const uint ClsCtxAll = 23;
    private const ushort VtLpWStr = 31;
    private const ushort VtBlob = 65;
    private const int MaximumFormatBlobBytes = 65536;
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    // Windows PKEY_Device_FriendlyName and PKEY_AudioEngine_DeviceFormat, also used
    // by NAudio's PropertyKeys. These are property identifiers, not registry writes.
    private static readonly NativePropertyKey FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly NativePropertyKey DeviceFormat = new(new("F19F064D-082C-4E27-BC73-6882A1BB8E4C"), 0);

    internal static IReadOnlyList<AudioDeviceInfo> GetRenderDevices() => GetRenderDevices(out _);

    internal static IReadOnlyList<AudioDeviceInfo> GetRenderDevices(out string? error)
    {
        error = null;
        IEndpointEnumerator? enumerator = null;
        IEndpointCollection? collection = null;
        try
        {
            enumerator = CreateEnumerator();
            string? defaultId = null;
            IEndpoint? defaultDevice = null;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out defaultDevice) >= 0)
                {
                    Marshal.ThrowExceptionForHR(defaultDevice.GetId(out defaultId));
                }
            }
            finally { Release(defaultDevice); }

            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(Render, Active, out collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var devices = new List<AudioDeviceInfo>();
            for (uint index = 0; index < count; index++)
            {
                IEndpoint? device = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                    Marshal.ThrowExceptionForHR(device.GetId(out var id));
                    devices.Add(new(id, ReadFriendlyName(device) ?? id,
                        string.Equals(id, defaultId, StringComparison.Ordinal)));
                }
                catch (COMException)
                {
                    // A device can disappear between collection creation and lookup.
                    error = "Some outputs changed during enumeration; the next refresh will retry.";
                }
                finally { Release(device); }
            }

            return devices.OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (COMException exception) { error = "Audio outputs are temporarily unavailable: " + exception.Message; return []; }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }

    internal static AudioDeviceInfo? GetDefaultRenderDevice()
    {
        IEndpointEnumerator? enumerator = null;
        IEndpoint? device = null;
        try
        {
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            return new(id, ReadFriendlyName(device) ?? id, true);
        }
        catch (COMException) { return null; }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    internal static WaveFormat? GetMixFormat(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        IEndpointEnumerator? enumerator = null;
        IEndpoint? device = null;
        object? activatedClient = null;
        var pointer = IntPtr.Zero;
        try
        {
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpoint, out device));
            var iid = typeof(IEndpointAudioClient).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out activatedClient));
            Marshal.ThrowExceptionForHR(((IEndpointAudioClient)activatedClient).GetMixFormat(out pointer));
            // NAudio copies WAVEFORMATEX/EXTENSIBLE into managed storage. No stream is
            // initialized; this neither opens playback nor changes the endpoint format.
            return pointer == IntPtr.Zero ? null : WaveFormat.MarshalFromPtr(pointer);
        }
        catch (COMException) { return null; }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
            Release(activatedClient);
            Release(device);
            Release(enumerator);
        }
    }

    internal static byte[]? ReadDeviceFormatBlob(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        IEndpointEnumerator? enumerator = null;
        IEndpoint? device = null;
        IEndpointPropertyStore? properties = null;
        var value = default(NativePropVariant);
        try
        {
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpoint, out device));
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0 /* STGM_READ */, out properties));
            var key = DeviceFormat;
            Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
            if (value.Type != VtBlob || value.Blob.Length < 18 ||
                value.Blob.Length > MaximumFormatBlobBytes || value.Blob.Data == IntPtr.Zero) return null;
            var bytes = new byte[checked((int)value.Blob.Length)];
            Marshal.Copy(value.Blob.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (COMException) { return null; }
        finally
        {
            PropVariantClear(ref value);
            Release(properties);
            Release(device);
            Release(enumerator);
        }
    }

    private static string? ReadFriendlyName(IEndpoint device)
    {
        IEndpointPropertyStore? properties = null;
        var value = default(NativePropVariant);
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0 /* STGM_READ */, out properties));
            var key = FriendlyName;
            Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out value));
            return value.Type == VtLpWStr && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        catch (COMException) { return null; }
        finally
        {
            PropVariantClear(ref value);
            Release(properties);
        }
    }

    private static IEndpointEnumerator CreateEnumerator() =>
        (IEndpointEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, throwOnError: true)!)!;

    private static void Release(object? value)
    {
        // Every caller owns exactly the one reference acquired by its COM out
        // parameter/activation, including aliases of an existing RCW. Never use
        // FinalReleaseComObject, which would invalidate somebody else's lease.
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref NativePropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePropertyKey(Guid format, uint id)
    {
        private readonly Guid _format = format;
        private readonly uint _id = id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob
    {
        public uint Length;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativePropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public NativeBlob Blob;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IEndpointCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IEndpoint device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IEndpoint device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IEndpoint device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpoint
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IEndpointPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out NativePropertyKey key);
        [PreserveSig] int GetValue(ref NativePropertyKey key, out NativePropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEndpointAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
    }
}
