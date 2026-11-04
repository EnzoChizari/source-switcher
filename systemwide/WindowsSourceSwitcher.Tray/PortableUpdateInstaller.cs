using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace WindowsSourceSwitcher.Tray;

internal static class PortableUpdateInstaller
{
    internal const string Marker = "source-switcher-portable.txt";
    private const string Launcher = "SourceSwitcher.exe";
    private static string InstallFolder => Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    public static bool CanInstall => string.Equals(Environment.ProcessPath, Path.Combine(InstallFolder, Launcher), StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(InstallFolder, Marker)) && Directory.GetParent(InstallFolder) is not null;

    public static async Task PrepareAndScheduleAsync(GitHubUpdateFeed feed, PortableRelease release, string token, CancellationToken cancellationToken)
    {
        if (!CanInstall) throw new InvalidOperationException("Install the full Source Switcher portable bundle first.");
        var install = InstallFolder;
        var parent = Directory.GetParent(install)!.FullName;
        if (File.GetAttributes(install).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Update a regular app folder instead of a junction or symbolic link.");
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(parent, ".source-switcher-stage-" + id);
        var backup = Path.Combine(parent, ".source-switcher-backup-" + id);
        var zip = Path.Combine(Path.GetTempPath(), $"source-switcher-{id}.zip");
        try
        {
            await feed.DownloadAsync(release, token, zip, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => Extract(zip, stage, release.Version, cancellationToken), cancellationToken).ConfigureAwait(false);
            BrowserBridgeInstaller.PreserveManifests(install, stage);
            cancellationToken.ThrowIfCancellationRequested();
            Schedule(install, stage, backup);
        }
        catch
        {
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            try { File.Delete(zip); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Extract(string zip, string stage, Version version, CancellationToken cancellationToken)
    {
        var prefix = $"SourceSwitcher-{version.ToString(3)}-win-x64/";
        Directory.CreateDirectory(stage);
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count is < 3 or > 4096) throw new InvalidDataException("Unexpected update package layout.");
        long total = 0;
        foreach (var item in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.FullName.StartsWith(prefix, StringComparison.Ordinal) || item.Length > 512L * 1024 * 1024
                || (total += item.Length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Unexpected update package path or size.");
            var relative = item.FullName[prefix.Length..];
            if (relative.EndsWith('/') || relative.Length == 0) continue;
            var parts = relative.Split('/');
            if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains('\\')))
                throw new InvalidDataException("Unsafe update package file name.");
            var target = Path.GetFullPath(Path.Combine(stage, Path.Combine(parts)));
            if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update package contains a path outside its folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            item.ExtractToFile(target);
        }
        foreach (var name in new[] { Launcher, Marker, "coreclr.dll", "WindowsSourceSwitcher.Tray.dll", "WindowsSourceSwitcher.Tray.runtimeconfig.json" })
            if (!File.Exists(Path.Combine(stage, name))) throw new InvalidDataException("The update package is incomplete.");
        if (!string.Equals(File.ReadAllText(Path.Combine(stage, Marker)).Trim(), version.ToString(3), StringComparison.Ordinal))
            throw new InvalidDataException("The package version does not match the release.");
    }

    private static void Schedule(string install, string stage, string backup)
    {
        // Only the authenticated, extracted sibling folder is eligible. The helper runs
        // outside the install directory so Windows can rename it after the tray exits.
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $install = {{Literal(install)}}
            $stage = {{Literal(stage)}}
            $backup = {{Literal(backup)}}
            $launcher = {{Literal(Launcher)}}
            $old = {{Environment.ProcessId}}
            $moved = $false
            try {
                $oldProcess = Get-Process -Id $old -ErrorAction SilentlyContinue
                if ($oldProcess -and -not $oldProcess.WaitForExit(45000)) { throw 'Source Switcher did not exit in time.' }
                for ($attempt = 0; $attempt -lt 20; $attempt++) {
                    try { [IO.Directory]::Move($install, $backup); $moved = $true; break }
                    catch [IO.IOException] { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 300 }
                }
                [IO.Directory]::Move($stage, $install)
                Start-Process -FilePath (Join-Path $install $launcher) -WorkingDirectory $install -WindowStyle Hidden -ErrorAction Stop
            } catch {
                $message = $_.Exception.Message
                if ($moved) {
                    try {
                        if ([IO.Directory]::Exists($install)) { [IO.Directory]::Move($install, $stage) }
                        [IO.Directory]::Move($backup, $install)
                    } catch { $message += "`nPrevious version: $backup" }
                }
                if ([IO.File]::Exists((Join-Path $install $launcher)) -and -not (Get-Process -Id $old -ErrorAction SilentlyContinue)) {
                    Start-Process -FilePath (Join-Path $install $launcher) -WorkingDirectory $install -WindowStyle Hidden -ErrorAction SilentlyContinue
                }
                Add-Type -AssemblyName System.Windows.Forms
                [void][Windows.Forms.MessageBox]::Show($message, 'Source Switcher update failed')
            }
            """;
        var options = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = Directory.GetParent(install)!.FullName,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            options.ArgumentList.Add(argument);
        using var process = Process.Start(options) ?? throw new InvalidOperationException("The update helper could not start.");
    }
}
