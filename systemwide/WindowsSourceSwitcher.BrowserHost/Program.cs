using System.IO.Pipes;
using System.Text.Json;
using WindowsSourceSwitcher.BrowserBridge;
using WindowsSourceSwitcher.Core;

namespace WindowsSourceSwitcher.BrowserHost;

/// <summary>Firefox/Zen and Helium native-messaging observation forwarder. Stdout is reserved for framed JSON.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var expectedEvidenceField = ValidateInvocation(args);
            _ = WindowsBrowserPeerIdentity.ValidateHostBrowserParent();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var pipe = new NamedPipeClientStream(".", WindowsBrowserPeerIdentity.CurrentPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            WindowsBrowserPeerIdentity.ValidateServer(pipe.SafePipeHandle,
                WindowsBrowserPeerIdentity.ExpectedTrayPathForHost(
                    Path.Combine(AppContext.BaseDirectory, "WindowsSourceSwitcher.BrowserHost.exe")));
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            while (true)
            {
                var requestBytes = await BrowserWireProtocol.ReadFrameAsync(pipe).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (requestBytes is null) return 0;
                var request = BrowserWireProtocol.ParseRequest(requestBytes);
                await BrowserWireProtocol.WriteFrameAsync(output, requestBytes).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                // A stalled browser must not keep a host blocked indefinitely after the tray
                // disconnects. Returning from Main also ends a pending standard-input read.
                var responseBytes = await BrowserWireProtocol.ReadFrameAsync(input).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (responseBytes is null) return 0;
                var response = BrowserWireProtocol.ParseResponse(responseBytes);
                if (response.EvidenceField != expectedEvidenceField
                    && !(expectedEvidenceField == BrowserWireProtocol.ChromiumEvidenceField
                        && response.EvidenceField is BrowserWireProtocol.ChromiumHeaderEvidenceField or BrowserWireProtocol.ChromiumStreamEvidenceField))
                    throw new InvalidDataException("The decoder evidence provider does not match the calling browser extension.");
                if (!string.Equals(response.ConnectionNonce, request.ConnectionNonce, StringComparison.Ordinal)
                    || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                    throw new InvalidDataException("Browser reply did not echo the receiver's current request.");
                await BrowserWireProtocol.WriteFrameAsync(pipe, responseBytes).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            // Browsers redirect stderr to extension diagnostics. Never print text to stdout.
            Console.Error.WriteLine("DX5 browser decoder bridge: " + error.Message);
            return 1;
        }
    }

    private static string ValidateInvocation(string[] args)
    {
        var chromium = args.Length is 1 or 2 && string.Equals(args[0], BrowserWireProtocol.ChromiumOrigin, StringComparison.Ordinal);
        if (chromium && args.Length == 2 && (!args[1].StartsWith("--parent-window=", StringComparison.Ordinal)
            || !ulong.TryParse(args[1].AsSpan("--parent-window=".Length), out _)))
            throw new UnauthorizedAccessException("Invalid Chromium native-host window argument.");
        if (!chromium && (args.Length != 2 || !string.Equals(args[1], BrowserWireProtocol.ExtensionId, StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("This host accepts only its configured Gecko or Helium extension invocation.");
        var expectedManifest = Path.Combine(AppContext.BaseDirectory, chromium
            ? BrowserWireProtocol.ChromiumManifestFileName : BrowserWireProtocol.GeckoManifestFileName);
        var manifest = chromium ? expectedManifest : Path.GetFullPath(args[0]);
        if (!string.Equals(manifest, expectedManifest, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Native manifest must belong to this app installation.");
        var info = new FileInfo(manifest);
        if (!info.Exists || info.Length is <= 0 or > 16 * 1024)
            throw new InvalidDataException("Native manifest is missing or exceeds its bounded size.");
        using var parsed = JsonDocument.Parse(File.ReadAllBytes(manifest), new JsonDocumentOptions { MaxDepth = 4 });
        var root = parsed.RootElement;
        if (root.GetProperty("name").GetString() != BrowserWireProtocol.HostName
            || root.GetProperty("type").GetString() != "stdio"
            || !string.Equals(Path.GetFullPath(root.GetProperty("path").GetString()!),
                Path.Combine(AppContext.BaseDirectory, "WindowsSourceSwitcher.BrowserHost.exe"), StringComparison.OrdinalIgnoreCase)
            || !HasOnlyAllowedExtension(root, chromium))
            throw new UnauthorizedAccessException("Native manifest does not match this host and its single allowed extension.");
        return chromium ? BrowserWireProtocol.ChromiumEvidenceField : BrowserWireProtocol.EvidenceField;
    }

    private static bool HasOnlyAllowedExtension(JsonElement root, bool chromium)
    {
        var property = chromium ? "allowed_origins" : "allowed_extensions";
        var opposite = chromium ? "allowed_extensions" : "allowed_origins";
        return !root.TryGetProperty(opposite, out _) && root.TryGetProperty(property, out var allowed)
            && allowed.ValueKind == JsonValueKind.Array && allowed.GetArrayLength() == 1
            && allowed[0].ValueKind == JsonValueKind.String
            && allowed[0].GetString() == (chromium ? BrowserWireProtocol.ChromiumOrigin : BrowserWireProtocol.ExtensionId);
    }
}
