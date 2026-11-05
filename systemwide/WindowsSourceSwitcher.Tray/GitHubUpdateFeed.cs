using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsSourceSwitcher.Tray;

internal sealed record PortableRelease(Version Version, long AssetId, long Bytes, string Sha256, string? Notes = null);

internal sealed class GitHubUpdateFeed(HttpClient http)
{
    internal const string Repository = "EnzoChizari/source-switcher";
    internal const string CredentialTarget = "SourceSwitcher:GitHub:" + Repository;
    internal const string LegacyAssetName = "SourceSwitcher-win-x64.zip";
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const string Api = "https://api.github.com/repos/" + Repository + "/releases/";

    public async Task<PortableRelease?> CheckAsync(Version installed, string token, CancellationToken cancellationToken)
    {
        using var request = Request(Api + "latest", "application/vnd.github+json", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub could not read the release feed. Check repository access and sign-in.");
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        await CopyBoundedAsync(stream, body, 1024 * 1024, cancellationToken);
        return ReadRelease(body.ToArray(), installed);
    }

    internal static PortableRelease? ReadRelease(ReadOnlyMemory<byte> response, Version installed)
    {
        using var json = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 16 });
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString();
        if (tag is null || !tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version)
            || version.Build < 0 || version.Revision >= 0 || version.ToString(3) != tag[1..]
            || version <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))) return null;
        var versionedName = $"SourceSwitcher-v{version.ToString(3)}-win-x64.zip";
        foreach (var asset in release.GetProperty("assets").EnumerateArray()
            .OrderByDescending(asset => asset.GetProperty("name").GetString() == versionedName))
        {
            var name = asset.GetProperty("name").GetString();
            if ((name != versionedName && name != LegacyAssetName) || asset.GetProperty("state").GetString() != "uploaded") continue;
            var digest = asset.GetProperty("digest").GetString();
            var id = asset.GetProperty("id").GetInt64();
            var size = asset.GetProperty("size").GetInt64();
            if (id > 0 && size is > 0 and <= MaximumPackageBytes
                && digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
                && digest[7..].All(Uri.IsHexDigit))
            {
                var notes = release.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
                    ? body.GetString()?.Trim() : null;
                if (notes is { Length: > 8000 }) notes = notes[..8000] + "\n…";
                return new PortableRelease(version, id, size, digest[7..], notes);
            }
        }
        throw new InvalidDataException("The latest release lacks a verified Windows x64 package.");
    }

    public async Task DownloadAsync(PortableRelease release, string token, string destination, CancellationToken cancellationToken)
    {
        using var request = Request(Api + "assets/" + release.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "application/octet-stream", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: true);
        await CopyBoundedAsync(source, file, release.Bytes, cancellationToken);
        if (file.Length != release.Bytes) throw new InvalidDataException("The update download is incomplete.");
        file.Position = 0;
        var digest = await SHA256.HashDataAsync(file, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("The update checksum does not match GitHub.");
    }

    private static HttpRequestMessage Request(string url, string accept, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SourceSwitcher");
        request.Headers.Accept.ParseAdd(accept);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("The GitHub response is larger than expected.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    internal static async Task<string?> ReadCredentialAsync(CancellationToken cancellationToken)
    {
        if (CredRead(CredentialTarget, 1, 0, out var pointer))
        {
            try
            {
                var value = Marshal.PtrToStructure<NativeCredential>(pointer);
                if (value.Size is > 0 and <= 4096 && value.Size % 2 == 0
                    && Marshal.PtrToStringUni(value.Blob, value.Size / 2)?.TrimEnd('\0') is { } stored && IsToken(stored))
                    return stored;
            }
            finally { CredFree(pointer); }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var manager = new[] { "ucrt64", "mingw64" }
            .Select(folder => Path.Combine(programFiles, "Git", folder, "bin", "git-credential-manager.exe"))
            .FirstOrDefault(File.Exists);
        if (manager is null) return null;
        var options = new ProcessStartInfo(manager, "get")
        {
            WorkingDirectory = programFiles,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        options.Environment["GCM_INTERACTIVE"] = "Never";
        options.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(options);
        if (process is null) return null;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync("protocol=https\nhost=github.com\n\n".AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            await error;
            if (process.ExitCode != 0) return null;
            var password = (await output).Split('\n').FirstOrDefault(line => line.StartsWith("password=", StringComparison.Ordinal))?[9..].TrimEnd('\r');
            return IsToken(password) ? password : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static bool IsToken(string? value) => value is { Length: >= 20 and <= 2048 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public nint Target, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int Size;
        public nint Blob;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
