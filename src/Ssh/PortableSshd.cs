using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dtssh.Auth;
using Dtssh.Infra;

namespace Dtssh.Ssh;

internal sealed record PortableSshd(string SshdPath, string SshKeygenPath, string SftpServerPath);

internal static class PortableSshdInstaller
{
    // renovate.json5 updates this release and both architecture checksums together.
    private const string Version = "10.0.0.0p2-Preview";
    private const string ReleaseBase =
        "https://github.com/PowerShell/Win32-OpenSSH/releases/download/" + Version;

    private sealed record Asset(string Archive, string Sha256);

    private static readonly IReadOnlyDictionary<Architecture, Asset> Assets =
        new Dictionary<Architecture, Asset>
        {
            [Architecture.X64] = new(
                "OpenSSH-Win64.zip",
                "23f50f3458c4c5d0b12217c6a5ddfde0137210a30fa870e98b29827f7b43aba5"),
            [Architecture.Arm64] = new(
                "OpenSSH-ARM64.zip",
                "698c6aec31c1dd0fb996206e8741f4531a97355686b5431ef347d531b07fcd42"),
        };

    public static string InstallDir() =>
        Path.Combine(Paths.BinDir(), "openssh-" + Version + "-" +
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());

    public static PortableSshd? Installed()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var dir = InstallDir();
        var bundle = Path.Combine(dir, BundleDirectoryName());
        var result = new PortableSshd(
            Path.Combine(bundle, "sshd.exe"),
            Path.Combine(bundle, "ssh-keygen.exe"),
            Path.Combine(bundle, "sftp-server.exe"));
        return File.Exists(result.SshdPath) &&
            File.Exists(result.SshKeygenPath) &&
            File.Exists(result.SftpServerPath)
            ? result
            : null;
    }

    public static async Task<PortableSshd> EnsureAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new DtsshException("portable Win32 OpenSSH is only available on Windows");

        var installed = Installed();
        if (installed is not null) return installed;

        if (!Assets.TryGetValue(RuntimeInformation.OSArchitecture, out var asset))
            throw new DtsshException(
                $"no portable Win32 OpenSSH build available for {RuntimeInformation.OSArchitecture}");

        Paths.EnsureDir(Paths.BinDir());
        await using var installLock = await AcquireInstallLockAsync(ct).ConfigureAwait(false);
        installed = Installed();
        if (installed is not null) return installed;

        var installDir = InstallDir();
        if (Directory.Exists(installDir))
        {
            TryDeleteDirectory(installDir);
            if (Directory.Exists(installDir))
                throw new DtsshException(
                    $"cannot replace incomplete portable OpenSSH install at {installDir}; " +
                    "close processes using that directory and try again");
        }
        var archivePath = Path.Combine(
            Paths.BinDir(), "openssh-dl-" + Guid.NewGuid().ToString("N") + ".zip");
        var extractDir = Path.Combine(
            Paths.BinDir(), "openssh-extract-" + Guid.NewGuid().ToString("N"));
        var url = ReleaseBase + "/" + asset.Archive;

        Console.Error.WriteLine(
            $"dtssh: portable OpenSSH not found - downloading {Version} for " +
            $"{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}...");
        Log.Debugf("download: fetching portable OpenSSH from {0} -> {1}", url, installDir);

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var stream = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var file = File.Create(archivePath))
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);

            await VerifyHashAsync(archivePath, asset.Sha256, ct).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(archivePath, extractDir);

            var bundle = Path.Combine(extractDir, BundleDirectoryName());
            foreach (var name in new[] { "sshd.exe", "ssh-keygen.exe", "sftp-server.exe" })
                if (!File.Exists(Path.Combine(bundle, name)))
                    throw new DtsshException(
                        $"portable OpenSSH archive is missing {name}");

            try
            {
                Directory.Move(extractDir, installDir);
            }
            catch (IOException) when (Installed() is not null)
            {
                // Another dtssh process completed the same installation.
            }
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(extractDir);
        }

        installed = Installed()
            ?? throw new DtsshException("portable OpenSSH installation did not complete");
        Console.Error.WriteLine($"dtssh: portable OpenSSH installed at {installDir}");
        return installed;
    }

    private static string BundleDirectoryName() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "OpenSSH-ARM64"
            : "OpenSSH-Win64";

    private static async Task VerifyHashAsync(
        string path, string expected, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new DtsshException(
                $"portable OpenSSH checksum mismatch (expected {expected}, got {actual})");
    }

    private static async Task<FileStream> AcquireInstallLockAsync(CancellationToken ct)
    {
        var path = Path.Combine(Paths.BinDir(), "openssh-install.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
