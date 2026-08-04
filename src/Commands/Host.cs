using System.Diagnostics;
using Dtssh.Auth;
using Dtssh.Connections;
using Dtssh.Discovery;
using Dtssh.Infra;
using Dtssh.Keys;
using Dtssh.Ssh;

namespace Dtssh.Commands;

// `dtssh host` — expose this machine for SSH over a dev tunnel. Runs a dedicated
// loopback sshd, provisions/reuses a discoverable tunnel, and hosts it in-process
// on the SDK relay (auto-reconnect + token refresh). Pairing is fully automatic:
// the tunnel carries discovery metadata so any client logged into the same
// devtunnel account finds it with `dtssh discover` — no bundle to copy.
internal static class HostCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var f = Flags.Parse(args, "system-sshd", "persist");
        var port = f.Int("port", 2222);
        var loginUser = f.Str("user");
        var aliasFlag = f.Str("alias");
        var tunnelFlag = f.Str("tunnel");
        var expiration = f.Str("expiration");
        var systemSshd = f.Bool("system-sshd", false);
        var persist = f.Bool("persist", false);

        if (args.Any(a => a is "-h" or "--help" or "help")) { PrintUsage(); return 0; }

        Paths.EnsureAll();
        var ct = CancellationToken.None;
        _ = await DevtunnelCli.EnsureBinaryAsync(ct).ConfigureAwait(false);

        var uname = string.IsNullOrEmpty(loginUser) ? Cli.CurrentUser() : loginUser;
        var hostName = Cli.Hostname();
        var al = aliasFlag;
        if (string.IsNullOrEmpty(al))
        {
            al = "dt-" + Cli.SanitizeAlias(hostName);
            var suffix = Wsl.Wsl.AliasSuffix();
            if (!string.IsNullOrEmpty(suffix)) al += "-" + suffix;
        }

        Log.Debugf("host: user={0} alias={1} port={2} systemSSHD={3} reuseTunnel={4}",
            uname, al, port, systemSshd, tunnelFlag);

        // 1. Client identity (authenticates the SSH session).
        var ident = persist
            ? LoadOrCreatePersistentIdentity(al)
            : Ed25519Identity.Generate("dtssh-client-" + al);
        var clientPub = ident.PublicLine;

        // 2. Prepare authentication + sshd.
        Process? sshdProc = null;
        var hostPub = "";
        if (systemSshd)
        {
            AuthorizeSystem(clientPub);
            Console.Error.WriteLine($"dtssh: authorized ephemeral key in {HomeSshDir()}/authorized_keys (system sshd)");
        }
        else
        {
            var cfg = await Sshd.PrepareAsync(port, clientPub, ct).ConfigureAwait(false);
            await cfg.ValidateAsync(ct).ConfigureAwait(false);
            var hk = await KeyStore.EnsureHostKeyAsync(ct: ct).ConfigureAwait(false);
            hostPub = hk.ReadPublicKey();
            var (file, sargs) = cfg.Command();
            sshdProc = Proc.Spawn(file, sargs, "[sshd] ");
            Console.Error.WriteLine($"dtssh: dedicated sshd listening on 127.0.0.1:{port}");
            if (!await Cli.WaitPortAsync(port, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            {
                TryKill(sshdProc);
                throw new DtsshException($"dedicated sshd did not open 127.0.0.1:{port}");
            }
        }

        try
        {
            return await HostLoopAsync(
                al, uname, hostName, port, expiration, persist, tunnelFlag,
                hostPub, ident, ct).ConfigureAwait(false);
        }
        finally
        {
            TryKill(sshdProc);
        }
    }

    private static async Task<int> HostLoopAsync(
        string al, string uname, string hostName, int port, string expiration,
        bool persist, string tunnelFlag, string hostPub,
        Ed25519Identity ident, CancellationToken ct)
    {
        // 3. Build discovery metadata for the tunnel description. The seed lets
        // any client on the same account reconstruct the SSH identity — this is
        // the sole (automatic) pairing mechanism.
        var labels = new List<string>();
        string? description = null;
        var discover = true;
        {
            var meta = new Discover.Meta(al, port, uname, hostPub, Ed25519Identity.SeedB64(ident.Seed), hostName);
            try
            {
                description = meta.Encode();
                labels.Add(Discover.Label);
                Log.Debugf("host: publishing discovery metadata ({0} bytes)", description.Length);
            }
            catch (DtsshException e)
            {
                Console.Error.WriteLine($"dtssh: auto-discovery disabled: {e.Message}");
                discover = false;
            }
        }

        // 4. Provision or reuse the tunnel (creation needs a user token → CLI).
        var effTunnelId = tunnelFlag;
        var createdFresh = string.IsNullOrEmpty(effTunnelId);
        if (persist && string.IsNullOrEmpty(tunnelFlag))
        {
            var saved = LoadPersistentTunnel(al);
            if (!string.IsNullOrEmpty(saved) && await DevtunnelCli.ExistsAsync(saved, ct).ConfigureAwait(false))
            {
                effTunnelId = saved;
                createdFresh = false;
                Log.Debugf("host: reusing persistent service tunnel {0}", saved);
            }
        }
        if (string.IsNullOrEmpty(effTunnelId))
        {
            effTunnelId = await DevtunnelCli.CreateAsync(
                port, string.IsNullOrEmpty(expiration) ? null : expiration,
                labels, description, ct).ConfigureAwait(false);
            createdFresh = true;
        }
        else if (discover && !persist)
        {
            Console.Error.WriteLine("dtssh: note: reusing an existing tunnel does not refresh discovery metadata; create a fresh tunnel for discovery.");
        }

        if (persist && createdFresh) SavePersistentTunnel(al, effTunnelId);

        // 5. Host in-process on the SDK relay with a host-scoped token.
        var hostToken = await DevtunnelCli.IssueTokenAsync(effTunnelId, "host", ct).ConfigureAwait(false);
        var trace = Relay.Trace();
        var mgmt = Relay.ManagementClient();
        var tunnel = await Relay.FetchTunnelAsync(mgmt, effTunnelId, hostToken, "host", ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var relayHost = await Relay.HostAsync(mgmt, tunnel, effTunnelId, trace, cts.Token).ConfigureAwait(false);
        Log.Debugf("host: tunnel ready, id={0}", effTunnelId);

        PrintBanner(effTunnelId, al, uname, discover);

        // 6. Block until interrupted; if the relay host stops on its own, exit
        // non-zero so a supervising service manager restarts us (auto-recovery).
        var stopping = false;
        var done = new TaskCompletionSource();
        void OnSignal(object? _, EventArgs __)
        {
            stopping = true;
            Console.Error.WriteLine("\ndtssh: tunnel host stopped.");
            cts.Cancel();
            done.TrySetResult();
        }
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; OnSignal(s, e); };
        AppDomain.CurrentDomain.ProcessExit += OnSignal;

        try { await relayHost.WaitForConnectionAndDisposeAsync(done.Task, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        if (stopping) return 0;
        Console.Error.WriteLine("dtssh: tunnel host exited unexpectedly");
        return 1;
    }

    // Waits until either the shutdown signal fires (clean stop) or the relay host
    // connection is lost (returns so the caller can exit non-zero for restart).
    private static async Task WaitForConnectionAndDisposeAsync(
        this Microsoft.DevTunnels.Connections.TunnelRelayTunnelHost host, Task stop, CancellationToken ct)
    {
        // The relay host auto-reconnects internally; we simply block on the stop
        // signal. Disposal happens when the process exits.
        await using (host)
        {
            var cancel = Task.Delay(Timeout.Infinite, ct);
            await Task.WhenAny(stop, cancel).ConfigureAwait(false);
        }
    }

    private static string PersistIdentityPath(string alias) =>
        Path.Combine(Paths.HostDir(), "service-" + Cli.SanitizeAlias(alias) + ".seed");

    private static string PersistTunnelPath(string alias) =>
        Path.Combine(Paths.HostDir(), "service-" + Cli.SanitizeAlias(alias) + ".tunnel");

    private static Ed25519Identity LoadOrCreatePersistentIdentity(string alias)
    {
        Paths.EnsureDir(Paths.HostDir());
        var p = PersistIdentityPath(alias);
        if (File.Exists(p))
        {
            try
            {
                var seed = Ed25519Identity.DecodeSeedB64(File.ReadAllText(p).Trim());
                return Ed25519Identity.FromSeed(seed, "dtssh-client-" + alias);
            }
            catch (Exception e) { Log.Debugf("host: stored identity seed unreadable ({0}); regenerating", e.Message); }
        }
        var ident = Ed25519Identity.Generate("dtssh-client-" + alias);
        File.WriteAllText(p, Ed25519Identity.SeedB64(ident.Seed) + "\n");
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        return ident;
    }

    private static string LoadPersistentTunnel(string alias)
    {
        try { return File.ReadAllText(PersistTunnelPath(alias)).Trim(); }
        catch { return ""; }
    }

    private static void SavePersistentTunnel(string alias, string tunnelId)
    {
        try
        {
            Paths.EnsureDir(Paths.HostDir());
            File.WriteAllText(PersistTunnelPath(alias), tunnelId + "\n");
        }
        catch (Exception e) { Console.Error.WriteLine($"dtssh: warning: could not persist tunnel id: {e.Message}"); }
    }

    private static string HomeSshDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ssh");
    }

    private static void AuthorizeSystem(string pub)
    {
        var sshDir = HomeSshDir();
        Directory.CreateDirectory(sshDir);
        var ak = Path.Combine(sshDir, "authorized_keys");
        var existing = File.Exists(ak) ? File.ReadAllText(ak) : "";
        if (existing.Contains(pub.Trim())) return;
        File.AppendAllText(ak, pub.Trim() + "\n");
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(ak, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    private static void TryKill(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    private static void PrintBanner(string tunnelId, string alias, string user, bool discoverable)
    {
        var line = new string('=', 72);
        Console.Write($"\n{line}\n");
        Console.Write($" dtssh host ready — tunnel \"{tunnelId}\", SSH user \"{user}\"\n");
        Console.Write($"{line}\n\n");
        if (discoverable)
        {
            Console.Write(" On any CLIENT logged into the same devtunnel account:\n\n");
            Console.Write($"     dtssh discover        # finds this host, wires up `ssh {alias}`\n");
            Console.Write($"     ssh {alias}\n\n");
            Console.Write(" The client needs the dtssh binary and the devtunnel CLI. No SSH\n");
            Console.Write(" password, key management, or host-key prompt is required.\n");
        }
        else
        {
            Console.Write(" WARNING: discovery metadata could not be published, so this host is\n");
            Console.Write(" not reachable. Re-run with a fresh tunnel to enable discovery.\n");
        }
        Console.Write($"{line}\n\n");
        Console.Error.WriteLine("dtssh: press Ctrl-C to stop hosting.");
    }

    private static void PrintUsage() => Console.Error.Write(
"""
dtssh host — expose this machine for SSH over a dev tunnel

USAGE:
    dtssh host [options]

Publishes discovery metadata so any client logged into the same devtunnel
account can reach this host with `dtssh discover` — no bundle to copy.

OPTIONS:
    --port N          loopback port for the dedicated sshd (default 2222)
    --user U          SSH login user (default: current user)
    --alias A         client-side host alias to suggest (default: dt-<hostname>)
    --tunnel ID       reuse an existing tunnel id (default: create one)
    --expiration D    tunnel expiration, e.g. 8h or 2d
    --system-sshd     use the system sshd/authorized_keys instead of a dedicated one
    --persist         reuse a stable identity + tunnel across restarts (services)

""");
}
