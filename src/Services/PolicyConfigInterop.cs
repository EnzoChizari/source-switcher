using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WindowsLosslessSwitcher.Services;

public static class PolicyConfigInterop
{
    public static WaveFormat? GetDeviceFormat(string deviceId)
    {
        var policyConfig = CreatePolicyConfig();
        var pointer = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(policyConfig.GetDeviceFormat(deviceId, defaultFormat: false, out pointer));
            return pointer == IntPtr.Zero ? null : WaveFormat.MarshalFromPtr(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
            Marshal.ReleaseComObject(policyConfig);
        }
    }

    public static void SetDeviceFormat(string deviceId, WaveFormat format)
    {
        var pointer = WaveFormat.MarshalToPtr(format);

        try
        {
            var policyConfig = CreatePolicyConfig();
            try
            {
                Marshal.ThrowExceptionForHR(policyConfig.SetDeviceFormat(deviceId, pointer, pointer));
            }
            finally
            {
                Marshal.ReleaseComObject(policyConfig);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static IPolicyConfig CreatePolicyConfig()
    {
        var policyConfigType = Type.GetTypeFromCLSID(PolicyConfigClientClsid, throwOnError: true)
            ?? throw new InvalidOperationException("Unable to resolve PolicyConfig COM type.");
        return (IPolicyConfig)Activator.CreateInstance(policyConfigType)!;
    }

    private static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Native methods return HRESULT. PreserveSig keeps that ABI so callers can check it
        // explicitly, without introducing an implicit managed retval parameter.
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mixFormat);
        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, out IntPtr format);
        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr propertyKey, IntPtr value);
        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr propertyKey, IntPtr value);
        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isVisible);
    }
}
