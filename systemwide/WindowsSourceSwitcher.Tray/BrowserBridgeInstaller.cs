using System.Text.Json;
using Microsoft.Win32;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.Tray;

internal static class BrowserBridgeInstaller
{
    internal static bool IsAvailable(string directory) =>
        File.Exists(Path.Combine(directory, "browser-helper", "WindowsSourceSwitcher.BrowserHost.exe"));

    internal static void PreserveManifests(string from, string to)
    {
        if (!IsAvailable(to)) return;
        foreach (var chromium in new[] { true, false })
        {
            var file = chromium ? BrowserWireProtocol.ChromiumManifestFileName : BrowserWireProtocol.GeckoManifestFileName;
            var source = Path.Combine(from, "browser-helper", file);
            if (IsOwned(source, chromium))
                File.Copy(source, Path.Combine(to, "browser-helper", file), overwrite: false);
        }
    }

    internal static string Register(string directory)
    {
        directory = Path.Combine(Path.GetFullPath(directory), "browser-helper");
        var host = Path.Combine(directory, "WindowsSourceSwitcher.BrowserHost.exe");
        if (!File.Exists(host)) throw new FileNotFoundException("Browser helper is missing. Use the complete published app folder.", host);
        foreach (var family in new[] { "Chromium", @"Google\Chrome" })
        {
            const bool chromium = true;
            var manifest = Path.Combine(directory, chromium ? BrowserWireProtocol.ChromiumManifestFileName : BrowserWireProtocol.GeckoManifestFileName);
            var keyPath = @"Software\" + family + @"\NativeMessagingHosts\" + BrowserWireProtocol.HostName;
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using (var existing = user.OpenSubKey(keyPath))
            {
                if (existing?.GetValue(null) is string previous && !string.Equals(previous, manifest, StringComparison.OrdinalIgnoreCase)
                    && !IsOwned(previous, chromium)) throw new InvalidOperationException("Another helper registration uses this name; it was preserved.");
            }
            if (File.Exists(manifest) && !IsOwned(manifest, chromium))
                throw new InvalidOperationException("An unrecognized manifest exists; it was preserved.");
            var data = new Dictionary<string, object>
            {
                ["name"] = BrowserWireProtocol.HostName, ["description"] = "DX5 chosen-tab audio metadata",
                ["path"] = host, ["type"] = "stdio",
                [chromium ? "allowed_origins" : "allowed_extensions"] = new[] { chromium ? BrowserWireProtocol.ChromiumOrigin : BrowserWireProtocol.ExtensionId }
            };
            File.WriteAllText(manifest, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            using var registration = user.CreateSubKey(keyPath, writable: true);
            registration.SetValue(null, manifest, RegistryValueKind.String);
        }
        return "Helper registered for this user. Load browser/chromium in Helium and choose Follow this tab.";
    }

    private static bool IsOwned(string path, bool chromium)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > 16384) return false;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            var allowed = root.GetProperty(chromium ? "allowed_origins" : "allowed_extensions");
            return root.GetProperty("name").GetString() == BrowserWireProtocol.HostName
                && root.GetProperty("type").GetString() == "stdio"
                && Path.GetFileName(root.GetProperty("path").GetString()) == "WindowsSourceSwitcher.BrowserHost.exe"
                && allowed.ValueKind == JsonValueKind.Array && allowed.GetArrayLength() == 1
                && allowed[0].GetString() == (chromium ? BrowserWireProtocol.ChromiumOrigin : BrowserWireProtocol.ExtensionId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return false; }
    }
}
