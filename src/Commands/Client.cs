using Dtssh.Auth;
using Dtssh.Discovery;
using Dtssh.Infra;
using Dtssh.Keys;
using Dtssh.Ssh;

namespace Dtssh.Commands;

// Client-side commands: discover, list, remove, connect, config, doctor.
// Pairing is fully automatic: `discover` enumerates dtssh-labelled tunnels on the
// account and reconstructs everything needed to connect. The C# proxy connects
// the tunnel in-process per invocation (no persistent forwarder process), so the
// Go `forward` status/stop concept and the "FORWARDER" column in `list` are dropped.

internal static class DiscoverCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var f = Flags.Parse(args, "q", "prune", "clean");
        if (args.Any(a => a is "-h" or "--help" or "help")) { Usage(); return 0; }
        var quiet = f.Bool("q", false);
        var prune = f.Bool("prune", false);
        var clean = f.Bool("clean", false);
        var ct = CancellationToken.None;

        Paths.EnsureAll();
        _ = await DevtunnelCli.EnsureBinaryAsync(ct).ConfigureAwait(false);

        var hosts = await Discover.ListAsync(ct).ConfigureAwait(false);
        if (hosts.Count == 0)
        {
            Console.WriteLine("dtssh: no discoverable hosts found on this devtunnel account.");
            Console.WriteLine("       On a host, run `dtssh host` to publish one.");
            return 0;
        }

        if (clean)
        {
            var deleted = new HashSet<string>();
            foreach (var s in Discover.Stale(hosts))
            {
                try { await DevtunnelCli.DeleteAsync(s.TunnelId, ct).ConfigureAwait(false); }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"dtssh: could not delete stale tunnel {s.TunnelId}: {e.Message}");
                    continue;
                }
                deleted.Add(s.TunnelId);
                if (!quiet)
                    Console.WriteLine($"dtssh: deleted stale tunnel {s.TunnelId} (superseded alias \"{s.Meta.Alias}\")");
            }
            if (deleted.Count > 0)
                hosts = hosts.Where(h => !deleted.Contains(h.TunnelId)).ToList();
        }

        var resolved = Discover.Resolve(hosts);
        if (resolved.Count == 0)
        {
            Console.WriteLine("dtssh: no discoverable hosts (matching tunnels had no dtssh metadata).");
            return 0;
        }

        var found = new HashSet<string>();
        foreach (var r in resolved)
        {
            try { Materialize(r.Alias, r.Host); }
            catch (Exception e) { Console.Error.WriteLine($"dtssh: skip \"{r.Alias}\": {e.Message}"); continue; }
            found.Add(r.Alias);
            var note = "";
            if (r.Ambiguous) note = "  (alias collided; suffixed with tunnel id)";
            if (r.Unconfirmed) note += "  (host connection unconfirmed — connecting will fail if stale)";
            if (quiet) Console.WriteLine(r.Alias);
            else Console.WriteLine($"dtssh: wired up {r.Alias,-24} ({r.Host.Meta.User}@ tunnel {r.Host.TunnelId}){note}");
        }

        if (prune)
        {
            foreach (var e in SshConfigFile.List())
            {
                if (found.Contains(e.Alias)) continue;
                SshConfigFile.Remove(e.Alias);
                TryDelete(Path.Combine(Paths.ClientDir(), e.Alias + ".key"));
                if (!quiet) Console.WriteLine($"dtssh: pruned stale alias \"{e.Alias}\"");
            }
        }

        if (!quiet)
        {
            var hint = resolved[0].Alias;
            foreach (var r in resolved)
                if (!r.Unconfirmed && !r.Ambiguous && found.Contains(r.Alias)) { hint = r.Alias; break; }
            Console.WriteLine($"\nConnect with:  ssh {hint}");
        }
        return 0;
    }

    // Installs the ssh_config alias, private key and host-key pin for a discovered
    // host, reconstructing the SSH key from the metadata seed.
    private static void Materialize(string alias, Discover.Host h)
    {
        var ident = h.Meta.Identity();
        var keyPath = KeyStore.WritePrivateKey(alias + ".key", ident.PrivatePem);
        var entry = new SshConfigFile.Entry
        {
            Alias = alias,
            User = h.Meta.User,
            Port = h.Meta.Port,
            TunnelId = h.TunnelId,
            IdentityFile = keyPath,
            HostPublicKey = h.Meta.HostPub,
            DtsshBin = Cli.SelfPath(),
        };
        entry.Install();
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    private static void Usage() => Console.Error.Write(
"""
dtssh discover — auto-discover dtssh hosts and wire up `ssh <alias>`

USAGE:
    dtssh discover [-q] [--prune] [--clean]

    -q        print only the aliases wired up
    --prune   remove local managed aliases no longer discoverable
    --clean   delete superseded stale tunnels (offline duplicates of a live host)

""");
}

internal static class ListCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        var f = Flags.Parse(args, "q");
        if (args.Any(a => a is "-h" or "--help" or "help"))
        {
            Console.Error.Write("dtssh list [-q]\n\nList dtssh-managed ssh_config host aliases.\n");
            return Task.FromResult(0);
        }
        var quiet = f.Bool("q", false);
        var entries = SshConfigFile.List();
        if (entries.Count == 0)
        {
            if (!quiet) Console.WriteLine("dtssh: no managed hosts. Discover them with: dtssh discover");
            return Task.FromResult(0);
        }
        if (quiet)
        {
            foreach (var e in entries) Console.WriteLine(e.Alias);
            return Task.FromResult(0);
        }
        Console.WriteLine($"{"ALIAS",-24} {"USER",-16} {"TUNNEL",-26} {"PORT",-7} PINNED");
        foreach (var e in entries)
            Console.WriteLine($"{e.Alias,-24} {e.User,-16} {e.TunnelId,-26} {e.Port,-7} {(e.Pinned ? "yes" : "no")}");
        return Task.FromResult(0);
    }
}

internal static class RemoveCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine("usage: dtssh remove <alias>");
            return Task.FromResult(2);
        }
        var alias = args[0];
        SshConfigFile.Remove(alias);
        TryDelete(Path.Combine(Paths.ClientDir(), alias + ".key"));
        TryDelete(Path.Combine(Paths.ClientDir(), alias + ".token"));
        Console.WriteLine($"dtssh: removed host alias \"{alias}\"");
        return Task.FromResult(0);
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}

internal static class ConnectCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: dtssh connect <alias> [ssh args...]");
            return Task.FromResult(2);
        }
        var ssh = Proc.Which("ssh") ?? throw new DtsshException("ssh not found in PATH");
        // Run ssh with inherited stdio and forward its exit code (portable
        // equivalent of exec; NativeAOT has no execve wrapper).
        return Proc.RunInteractiveAsync(ssh, args);
    }
}

internal static class ConfigCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var f = Flags.Parse(args);
        var tunnelId = f.Arg(0);
        if (string.IsNullOrEmpty(tunnelId) || args.Any(a => a is "-h" or "--help"))
        {
            Console.Error.WriteLine("usage: dtssh config <tunnel-id> [--port N] [--user U] [--alias A]");
            return string.IsNullOrEmpty(tunnelId) ? 2 : 0;
        }
        var client = await KeyStore.EnsureClientIdentityAsync().ConfigureAwait(false);
        var entry = new SshConfigFile.Entry
        {
            Alias = f.Str("alias", "dt-host"),
            User = f.Str("user", Cli.CurrentUser()),
            Port = f.Int("port", 2222),
            TunnelId = tunnelId,
            IdentityFile = client.PrivatePath,
            DtsshBin = Cli.SelfPath(),
        };
        Console.Write(entry.Block());
        return 0;
    }
}

internal static class DoctorCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        const string ok = "  [ok] ";
        const string bad = "  [!!] ";
        Console.WriteLine("dtssh doctor:");

        try { Console.WriteLine($"{ok}devtunnel: {DevtunnelCli.Binary()}"); }
        catch (Exception e) { Console.WriteLine($"{bad}devtunnel: {e.Message}"); }

        Report(ok, bad, "ssh");
        Report(ok, bad, "ssh-keygen");

        try { Console.WriteLine($"{ok}sshd: {Sshd.SshdPath()}"); }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            Console.WriteLine($"{ok}sshd: portable OpenSSH (downloaded on first host start)");
        }
        catch (Exception e) { Console.WriteLine($"{bad}sshd: {e.Message} (needed only on the host)"); }

        Console.WriteLine($"  data dir:  {Paths.DataRoot()}");
        Console.WriteLine($"  state dir: {Paths.StateRoot()}");
        Console.WriteLine($"  cache dir: {Paths.CacheRoot()}");
        return Task.FromResult(0);
    }

    private static void Report(string ok, string bad, string tool)
    {
        var p = Proc.Which(tool);
        Console.WriteLine(p is not null ? $"{ok}{tool}: {p}" : $"{bad}{tool}: not found");
    }
}
