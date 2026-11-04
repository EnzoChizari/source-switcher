using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WindowsSourceSwitcher.Tray;
using Xunit;

namespace WindowsSourceSwitcher.Tray.Tests;

public sealed class GitHubUpdateTests
{
    [Theory]
    [InlineData("v1.1.0", false, false, true)]
    [InlineData("v1.1.0", true, false, false)]
    [InlineData("v1.1.0", false, true, false)]
    [InlineData("v1.0.9", false, false, false)]
    [InlineData("v1.0.8", false, false, false)]
    [InlineData("v1.1.0-preview", false, false, false)]
    public void OffersOnlyNewerStableVersions(string tag, bool draft, bool prerelease, bool expected)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = tag, draft, prerelease, body = "Changes in this update.",
            assets = new[] { new { name = GitHubUpdateFeed.LegacyAssetName, state = "uploaded", id = 7, size = 123, digest = "sha256:" + new string('a', 64) } }
        });

        var release = GitHubUpdateFeed.ReadRelease(json, new Version(1, 0, 9, 0));
        Assert.Equal(expected, release is not null);
        if (expected) Assert.Equal("Changes in this update.", release!.Notes);
    }

    [Fact]
    public void PrefersTheVersionedPackageOverTheCompatibilityCopy()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tag_name = "v1.3.1", draft = false, prerelease = false,
            assets = new[]
            {
                new { name = GitHubUpdateFeed.LegacyAssetName, state = "uploaded", id = 7, size = 123, digest = "sha256:" + new string('a', 64) },
                new { name = "SourceSwitcher-v1.3.1-win-x64.zip", state = "uploaded", id = 8, size = 123, digest = "sha256:" + new string('a', 64) }
            }
        });
        var release = GitHubUpdateFeed.ReadRelease(json, new Version(1, 3, 0));
        Assert.Equal(8, release?.AssetId);
    }

    [Theory]
    [InlineData(4, true, true)]
    [InlineData(5, true, false)]
    [InlineData(4, false, false)]
    public async Task DownloadChecksSizeAndDigest(int size, bool correctHash, bool expected)
    {
        var payload = "test"u8.ToArray();
        var digest = correctHash ? Convert.ToHexString(SHA256.HashData(payload)) : new string('0', 64);
        using var client = new HttpClient(new AssetHandler(payload));
        var feed = new GitHubUpdateFeed(client);
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var download = feed.DownloadAsync(new(new Version(1, 1, 0), 7, size, digest), "test", file, CancellationToken.None);
            if (expected)
            {
                await download;
                Assert.Equal(payload, File.ReadAllBytes(file));
            }
            else await Assert.ThrowsAsync<InvalidDataException>(() => download);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void ExtractionRejectsPathsOutsideTheStagedFolder()
    {
        var directory = Directory.CreateTempSubdirectory("source-switcher-update-");
        try
        {
            var zip = Path.Combine(directory.FullName, "update.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var path in new[] { "SourceSwitcher.exe", "../outside.txt", PortableUpdateInstaller.Marker })
                {
                    using var output = archive.CreateEntry("SourceSwitcher-1.1.0-win-x64/" + path).Open();
                    output.Write(Encoding.UTF8.GetBytes("data"));
                }
            Assert.Throws<InvalidDataException>(() => PortableUpdateInstaller.Extract(zip, Path.Combine(directory.FullName, "stage"),
                new Version(1, 1, 0), CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(directory.FullName, "outside.txt")));
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class AssetHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("application/octet-stream", Assert.Single(request.Headers.Accept).MediaType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}
