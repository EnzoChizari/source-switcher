using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using WindowsLosslessSwitcher.Models;

namespace WindowsLosslessSwitcher.Services;

// The Revolution mixer fixes the WDM format to its hardware clock. IsFormatSupported
// therefore cannot enumerate other rates. Use the installed vendor API (ABI 5.13),
// never a redistributed DLL, registry clock override, ASIO stream or UI automation.
internal static class RevolutionDeviceController
{
    private static readonly PropertyKey ContainerIdKey = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly object Gate = new();
    private sealed record Binding(string Name, Guid Container);
    private static readonly ConcurrentDictionary<string, Binding> Bindings = new(StringComparer.OrdinalIgnoreCase);
    internal sealed record Clock(int Rate, int[] Rates);

    internal static bool IsRevolution(MMDevice device) =>
        device.FriendlyName.EndsWith("(Revolution 2x2)", StringComparison.OrdinalIgnoreCase);

    // The vendor mixer can recreate WDM endpoints for a different rate. Never bind
    // by name alone: preserve both physical container and logical output (analog/virtual).
    internal static string ResolveEndpoint(string endpointId)
    {
        if (Bindings.TryGetValue(endpointId, out var known) && known.Container == Guid.Empty) return endpointId;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var binding = Bindings.GetOrAdd(endpointId, id =>
            {
                using var endpoint = enumerator.GetDevice(id);
                return IsRevolution(endpoint) && endpoint.Properties.Contains(ContainerIdKey)
                    && Guid.TryParse(endpoint.Properties[ContainerIdKey].Value?.ToString(), out var container)
                    ? new(endpoint.FriendlyName, container) : new(string.Empty, Guid.Empty);
            });
            if (binding.Container == Guid.Empty) return endpointId;
            using (var endpoint = enumerator.GetDevice(endpointId))
                if (endpoint.State == DeviceState.Active) return endpointId;
            var candidates = new List<(string Id, string Name, Guid Container)>();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (endpoint)
                    if (endpoint.Properties.Contains(ContainerIdKey)
                        && Guid.TryParse(endpoint.Properties[ContainerIdKey].Value?.ToString(), out var container))
                        candidates.Add((endpoint.ID, endpoint.FriendlyName, container));
            }
            return FindReplacement(binding.Name, binding.Container, candidates) ?? endpointId;
        }
        catch { return endpointId; }
    }

    internal static string? FindReplacement(string name, Guid container,
        IEnumerable<(string Id, string Name, Guid Container)> candidates)
    {
        if (container == Guid.Empty) return null;
        var matches = candidates.Where(candidate => candidate.Name == name && candidate.Container == container).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    internal static bool TryRead(MMDevice device, out Clock? clock, out string error)
    {
        clock = null;
        error = string.Empty;
        if (!IsRevolution(device)) return false;
        try
        {
            lock (Gate)
            {
                using var api = new Driver(device);
                clock = api.Read();
            }
            return true;
        }
        catch (Exception ex) { error = "Revolution driver: " + ex.Message; return false; }
    }

    internal static bool TrySet(MMDevice device, AudioFormatCandidate target, out string error)
    {
        try
        {
            lock (Gate)
            {
                using var api = new Driver(device);
                var clock = api.Read();
                if (!clock.Rates.Contains(target.SampleRateHz))
                    throw new InvalidOperationException("The driver does not advertise the requested rate.");
                if (clock.Rate != target.SampleRateHz)
                {
                    api.Set(target.SampleRateHz);
                }
                if (api.Read().Rate != target.SampleRateHz)
                    throw new InvalidOperationException("Hardware clock readback did not confirm the requested rate.");
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { error = "Revolution driver: " + ex.Message; return false; }
    }

    // Preserve the driver's actual PCM container, valid bits and channel mask. Rate
    // support does not imply support for every bit depth or a 32-bit physical converter.
    internal static IReadOnlyList<EndpointFormatDescriptor> BuildFormats(EndpointFormatDescriptor current, IEnumerable<int> rates)
    {
        var result = new List<EndpointFormatDescriptor>();
        foreach (var rate in rates.Where(rate => rate is >= 8000 and <= 768000).Distinct().Order())
        {
            var blob = EndpointFormatSupport.SerializeWaveFormat(current.NativeFormat);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), rate);
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(8), checked(rate * current.NativeFormat.BlockAlign));
            if (EndpointFormatSupport.TryParseDeviceFormat(blob, EndpointFormatOrigin.DriverApi) is { } format)
                result.Add(format);
        }
        return result;
    }

    private sealed class Driver : IDisposable
    {
        private IntPtr _library;
        private uint _handle;
        private readonly HandleOut _rate;
        private readonly Rates _rates;
        private readonly HandleValue _set;
        private readonly HandleOnly _close;

        internal Driver(MMDevice device)
        {
            if (!IsRevolution(device) || !device.Properties.Contains(ContainerIdKey) ||
                !Guid.TryParse(device.Properties[ContainerIdKey].Value?.ToString(), out var container) || container == Guid.Empty)
                throw new InvalidOperationException("Cannot identify this Revolution endpoint's physical device.");
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Black Lion Audio", "USB Audio Device Driver", "x64", "BlackLionUsbAudioapi_x64.dll");
            try
            {
                _library = NativeLibrary.Load(path, typeof(Driver).Assembly, DllImportSearchPath.SafeDirectories);
                if (Get<NoArgs>("GetApiVersion")() != 0x0005000D)
                    throw new InvalidOperationException("Unsupported vendor API version; tested with Black Lion driver 5.72 (API 5.13).");
                _close = Get<HandleOnly>("CloseDevice");
                _rate = Get<HandleOut>("GetCurrentSampleRate");
                _rates = Get<Rates>("GetSupportedSampleRates");
                _set = Get<HandleValue>("SetSampleRate");
                Check(Get<NoArgs>("EnumerateDevices")(), "enumerate devices");
                var count = Get<NoArgs>("GetDeviceCount")();
                if (count > 16) throw new InvalidOperationException("Unexpected vendor device count.");
                var open = Get<HandleOut>("OpenDeviceByIndex");
                var containerId = Get<StringValue>("GetDeviceContainerIdString");
                var instanceId = Get<StringValue>("GetDeviceInstanceIdString");
                for (uint index = 0; index < count; index++)
                {
                    Check(open(index, out var candidate), "open device");
                    try
                    {
                        var text = new StringBuilder(1024);
                        Check(containerId(candidate, text, 1024), "read container identity");
                        if (!Guid.TryParse(text.ToString(), out var id) || id != container) continue;
                        text.Clear();
                        Check(instanceId(candidate, text, 1024), "read USB identity");
                        if (!text.ToString().StartsWith(@"USB\VID_32F0&PID_0001\", StringComparison.OrdinalIgnoreCase)) continue;
                        _handle = candidate;
                        break;
                    }
                    finally { if (candidate != _handle) _close(candidate); }
                }
                if (_handle == 0) throw new InvalidOperationException("No vendor device matches the selected endpoint.");
            }
            catch { Dispose(); throw; }
        }

        internal Clock Read()
        {
            Check(_rate(_handle, out var rate), "read hardware clock");
            var values = new uint[32];
            Check(_rates(_handle, (uint)values.Length, values, out var count), "read supported rates");
            if (count is 0 or > 32 || rate is < 8000 or > 768000 ||
                values.Take((int)count).Any(value => value is < 8000 or > 768000))
                throw new InvalidOperationException("Invalid rate data returned by the driver.");
            return new((int)rate, values.Take((int)count).Select(value => (int)value).Distinct().Order().ToArray());
        }

        internal void Set(int rate) => Check(_set(_handle, (uint)rate), "set hardware clock (check clock source and active clients)");
        private T Get<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, "TUSBAUDIO_" + name));
        private static void Check(uint status, string action)
        {
            if (status != 0) throw new InvalidOperationException($"Cannot {action}: status 0x{status:X8}.");
        }
        public void Dispose()
        {
            if (_handle != 0) { _close(_handle); _handle = 0; }
            if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint HandleOnly(uint handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint HandleOut(uint handle, out uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint HandleValue(uint handle, uint value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint Rates(uint handle, uint capacity, [Out] uint[] values, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate uint StringValue(uint handle, StringBuilder value, uint capacity);
    }
}
