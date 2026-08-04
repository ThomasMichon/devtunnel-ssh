using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dtssh.Infra;
using Dtssh.Json;

namespace Dtssh.Auth;

// Thin bridge over the Microsoft `devtunnel` CLI. dtssh keeps using the CLI for
// interactive login and short-lived tunnel management/token issuance, while the
// long-lived host/client connections run in-process on the DevTunnels SDK.
internal static class DevtunnelCli
{
    private static string? cached;

    // Resolves the devtunnel executable, honouring DTSSH_DEVTUNNEL.
    public static string Binary()
    {
        if (cached is not null) return cached;

        var env = Environment.GetEnvironmentVariable("DTSSH_DEVTUNNEL");
        if (!string.IsNullOrEmpty(env)) return cached = env;

        var onPath = Which("devtunnel");
        if (onPath is not null) return cached = onPath;

        foreach (var c in DefaultLocations())
            if (File.Exists(c)) return cached = c;

        throw new DtsshException(
            "devtunnel CLI not found; run `dtssh login` (auto-downloads it), " +
            "install from https://aka.ms/devtunnels/download, or set DTSSH_DEVTUNNEL");
    }

    public static string ManagedPath()
    {
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "devtunnel.exe" : "devtunnel";
        return Path.Combine(Paths.BinDir(), name);
    }

    // Official Microsoft Dev Tunnels CLI direct-download aka.ms links, keyed by
    // "os/arch". macOS variants are zip archives; the rest are raw executables.
    private static readonly Dictionary<string, string> DownloadUrls = new()
    {
        ["windows/x64"] = "https://aka.ms/TunnelsCliDownload/win-x64",
        // Microsoft does not publish a native Windows ARM64 CLI; use the x64
        // build through Windows' built-in x64 emulation.
        ["windows/arm64"] = "https://aka.ms/TunnelsCliDownload/win-x64",
        ["osx/arm64"] = "https://aka.ms/TunnelsCliDownload/osx-arm64-zip",
        ["osx/x64"] = "https://aka.ms/TunnelsCliDownload/osx-x64-zip",
        ["linux/x64"] = "https://aka.ms/TunnelsCliDownload/linux-x64",
        ["linux/arm64"] = "https://aka.ms/TunnelsCliDownload/linux-arm64",
    };

    private static string PlatformKey()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        return os + "/" + arch;
    }

    // Resolves the devtunnel CLI, auto-downloading the official build into dtssh's
    // managed bin dir if it is not already available. Progress is written to
    // stderr. Used by login/host/discover/service so first-time setup needs no
    // manual CLI install.
    public static async Task<string> EnsureBinaryAsync(CancellationToken ct = default)
    {
        try { return Binary(); }
        catch (DtsshException) { return await DownloadAsync(ct).ConfigureAwait(false); }
    }

    // Fetches the official devtunnel CLI for the current platform into dtssh's
    // managed bin dir and returns its path, replacing any existing copy.
    public static async Task<string> DownloadAsync(CancellationToken ct = default)
    {
        var key = PlatformKey();
        if (!DownloadUrls.TryGetValue(key, out var url))
            throw new DtsshException(
                $"no devtunnel CLI download available for {key}; install manually from " +
                "https://aka.ms/devtunnels/download or set DTSSH_DEVTUNNEL");

        Paths.EnsureDir(Paths.BinDir());
        var dest = ManagedPath();
        Console.Error.WriteLine($"dtssh: devtunnel CLI not found — downloading official build for {key}...");
        Log.Debugf("download: fetching devtunnel CLI from {0} -> {1}", url, dest);

        var tmp = Path.Combine(Paths.BinDir(), "devtunnel-dl-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var stream = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var file = File.Create(tmp))
                await stream.CopyToAsync(file, ct).ConfigureAwait(false);

            // macOS ships the CLI inside a zip; everything else is a raw executable.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                ExtractZipBinary(tmp, dest);
            else
                File.Copy(tmp, dest, overwrite: true);

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                try { File.SetUnixFileMode(dest, (UnixFileMode)0b111_101_101); } catch { }

            // Freshly downloaded devtunnel prints a one-time first-run notice to
            // stdout, which would corrupt the first `-j` parse. Warm it up once
            // (discarding output) so real commands see clean JSON.
            cached = dest;
            try { await Proc.RunAsync(dest, new[] { "--version" }, ct).ConfigureAwait(false); } catch { }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }

        cached = dest;
        Console.Error.WriteLine($"dtssh: devtunnel CLI installed at {dest}");
        return dest;
    }

    private static void ExtractZipBinary(string zipPath, string dest)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            Path.GetFileName(e.FullName) == "devtunnel" && !e.FullName.EndsWith('/'))
            ?? throw new DtsshException("devtunnel binary not found inside downloaded zip");
        using var src = entry.Open();
        using var outFile = File.Create(dest);
        src.CopyTo(outFile);
    }

    private static IEnumerable<string> DefaultLocations()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return ManagedPath();
        yield return Path.Combine(home, "bin", "devtunnel");
        yield return "/usr/local/bin/devtunnel";
        yield return "/tmp/devtunnel";
    }

    private static string? Which(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var names = isWin ? new[] { exe + ".exe", exe + ".cmd", exe } : new[] { exe };
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var n in names)
            {
                var full = Path.Combine(dir, n);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    // Runs `devtunnel user login` interactively, passing extra flags through.
    // Auto-downloads the CLI first if it's not already installed.
    public static async Task<int> LoginAsync(IReadOnlyList<string> extraArgs, CancellationToken ct = default)
    {
        var bin = await EnsureBinaryAsync(ct).ConfigureAwait(false);
        var args = new List<string> { "user", "login" };
        args.AddRange(extraArgs);
        return await Proc.RunInteractiveAsync(bin, args, ct).ConfigureAwait(false);
    }

    // Issues a tunnel access token with the given scope ("host" or "connect").
    public static async Task<string> IssueTokenAsync(string tunnelId, string scope, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Binary(), new[] { "token", tunnelId, "--scopes", scope, "-j" }, ct)
            .ConfigureAwait(false);
        if (!r.Ok)
            throw new DtsshException($"issue {scope} token for {tunnelId}: {r.StderrTrim}");

        try
        {
            var parsed = JsonSerializer.Deserialize(r.Stdout, AppJsonContext.Default.DevtunnelTokenResult);
            if (!string.IsNullOrEmpty(parsed?.Token)) return parsed!.Token!;
        }
        catch (JsonException) { /* fall through to bare-token scrape */ }

        var bare = r.StdoutTrim;
        if (bare.Length > 0 && !bare.Any(char.IsWhiteSpace)) return bare;
        throw new DtsshException($"could not parse {scope} token for {tunnelId}");
    }

    // `devtunnel show <id> -j` -> tunnel, or null if it doesn't exist / not owned.
    public static async Task<CliTunnel?> ShowAsync(string tunnelId, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Binary(), new[] { "show", tunnelId, "-j" }, ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        try
        {
            var show = JsonSerializer.Deserialize(r.Stdout, AppJsonContext.Default.CliShowResult);
            return show?.Tunnel;
        }
        catch (JsonException) { return null; }
    }

    public static async Task<bool> ExistsAsync(string tunnelId, CancellationToken ct = default)
        => tunnelId.Length > 0 && await ShowAsync(tunnelId, ct).ConfigureAwait(false) is not null;

    public static async Task DeleteAsync(string tunnelId, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Binary(), new[] { "delete", tunnelId }, ct).ConfigureAwait(false);
        if (!r.Ok) throw new DtsshException($"devtunnel delete {tunnelId}: {r.StderrTrim}");
    }

    // Lists tunnels carrying the given label, decoding the terse `-j` output.
    public static async Task<IReadOnlyList<CliTunnel>> ListByLabelAsync(string label, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Binary(),
            new[] { "list", "--labels", label, "-j" }, ct).ConfigureAwait(false);
        if (!r.Ok)
            throw new DtsshException(
                $"devtunnel list failed (are you logged in? run `dtssh login`): {r.StderrTrim}");
        var trimmed = r.StdoutTrim;
        if (trimmed.Length == 0) return Array.Empty<CliTunnel>();
        try
        {
            if (trimmed.StartsWith('['))
                return JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.CliTunnelArray)
                    ?? Array.Empty<CliTunnel>();
            var obj = JsonSerializer.Deserialize(trimmed, AppJsonContext.Default.CliListResult);
            return (IReadOnlyList<CliTunnel>?)obj?.Tunnels ?? obj?.Value ?? Array.Empty<CliTunnel>();
        }
        catch (JsonException e)
        {
            throw new DtsshException($"parse devtunnel list output: {e.Message}");
        }
    }

    // Creates a new tunnel with the given port, labels and description, returning
    // its full id (name.cluster). Tunnel creation needs a user token, so it goes
    // through the CLI; hosting then runs in-process on the SDK.
    public static async Task<string> CreateAsync(
        int port, string? expiration, IReadOnlyList<string> labels,
        string? description, CancellationToken ct = default)
    {
        var args = new List<string> { "create", "-j" };
        if (!string.IsNullOrEmpty(expiration)) { args.Add("--expiration"); args.Add(expiration!); }
        foreach (var l in labels) if (!string.IsNullOrEmpty(l)) { args.Add("--labels"); args.Add(l); }
        if (!string.IsNullOrEmpty(description)) { args.Add("--description"); args.Add(description!); }

        var r = await Proc.RunAsync(Binary(), args, ct).ConfigureAwait(false);
        if (!r.Ok) throw new DtsshException($"devtunnel create: {r.StderrTrim}");
        var t = ParseTunnel(r.Stdout) ?? throw new DtsshException("could not parse created tunnel id");
        var id = FullId(t);
        await PortCreateAsync(id, port, ct).ConfigureAwait(false);
        return id;
    }

    // Adds a TCP port to an existing tunnel.
    public static async Task PortCreateAsync(string tunnelId, int port, CancellationToken ct = default)
    {
        var r = await Proc.RunAsync(Binary(),
            new[] { "port", "create", tunnelId, "-p", port.ToString(), "--protocol", "auto" }, ct)
            .ConfigureAwait(false);
        if (!r.Ok) throw new DtsshException($"devtunnel port create {tunnelId} :{port}: {r.StderrTrim}");
    }

    // The full tunnel id, joining name + cluster when the CLI reports them apart.
    public static string FullId(CliTunnel t)
    {
        var id = t.TunnelId ?? "";
        if (!string.IsNullOrEmpty(t.ClusterId) && !id.Contains('.'))
            id += "." + t.ClusterId;
        return id;
    }

    private static CliTunnel? ParseTunnel(string json)
    {
        try
        {
            var show = JsonSerializer.Deserialize(json, AppJsonContext.Default.CliShowResult);
            if (show?.Tunnel is not null) return show.Tunnel;
        }
        catch (JsonException) { }
        try { return JsonSerializer.Deserialize(json, AppJsonContext.Default.CliTunnel); }
        catch (JsonException) { return null; }
    }
}

// Domain error whose message is shown to the user (without a stack trace).
internal sealed class DtsshException : Exception
{
    public DtsshException(string message) : base(message) { }
}
