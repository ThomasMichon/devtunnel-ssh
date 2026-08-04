using Dtssh.Auth;
using Dtssh.Infra;

namespace Dtssh.Keys;

// Manages the on-disk SSH identities dtssh uses. Keys are generated with the
// OpenSSH `ssh-keygen`, guaranteeing the files are in a format the local
// ssh/sshd binaries accept.
//
// Ported from the Go internal/keys package; paths and semantics are unchanged.
internal readonly record struct KeyPair(string PrivatePath, string PublicPath)
{
    public string ReadPublicKey() => File.ReadAllText(PublicPath).Trim();
    public string ReadPrivateKey() => File.ReadAllText(PrivatePath);
}

internal static class KeyStore
{
    // The ephemeral client identity that authenticates the SSH session; the tunnel
    // itself provides transport authorization. Generated on first use.
    public static async Task<KeyPair> EnsureClientIdentityAsync(CancellationToken ct = default)
    {
        Paths.EnsureDir(Paths.ClientDir());
        var priv = Path.Combine(Paths.ClientDir(), "id_ed25519");
        return await EnsureAsync(priv, "dtssh-client", null, ct).ConfigureAwait(false);
    }

    // The dedicated sshd host key. Generated on first use.
    public static async Task<KeyPair> EnsureHostKeyAsync(
        string? sshKeygenPath = null, CancellationToken ct = default)
    {
        Paths.EnsureDir(Paths.HostDir());
        var priv = Path.Combine(Paths.HostDir(), "ssh_host_ed25519_key");
        return await EnsureAsync(priv, "dtssh-host", sshKeygenPath, ct).ConfigureAwait(false);
    }

    private static async Task<KeyPair> EnsureAsync(
        string privPath, string comment, string? sshKeygenPath, CancellationToken ct)
    {
        var pair = new KeyPair(privPath, privPath + ".pub");
        if (FileExists(pair.PrivatePath) && FileExists(pair.PublicPath))
            return pair;

        // Clean up a half-written keypair before regenerating.
        TryDelete(pair.PrivatePath);
        TryDelete(pair.PublicPath);

        var kg = sshKeygenPath ?? Proc.Which("ssh-keygen")
            ?? throw new DtsshException("ssh-keygen not found in PATH: install OpenSSH");

        var res = await Proc.RunAsync(kg,
            new[] { "-t", "ed25519", "-N", "", "-C", comment, "-f", pair.PrivatePath }, ct)
            .ConfigureAwait(false);
        if (res.ExitCode != 0)
            throw new DtsshException($"ssh-keygen failed: {res.Stderr.Trim()}{res.Stdout.Trim()}");

        ChmodPrivate(pair.PrivatePath);
        return pair;
    }

    // Writes a private key blob to disk with strict permissions and returns the path.
    // Used by `dtssh discover` to materialize a discovered host's client key.
    public static string WritePrivateKey(string name, string blob)
    {
        Paths.EnsureDir(Paths.ClientDir());
        var p = Path.Combine(Paths.ClientDir(), name);
        if (!blob.EndsWith('\n')) blob += "\n";
        File.WriteAllText(p, blob);
        ChmodPrivate(p);
        return p;
    }

    private static void ChmodPrivate(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best effort */ }
        }
    }

    private static bool FileExists(string p) => File.Exists(p);

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
    }
}
