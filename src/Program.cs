using System.Reflection;
using Dtssh.Commands;
using Dtssh.Infra;
using Dtssh.Json;

namespace Dtssh;

internal static class Program
{
    // Sourced from the assembly's InformationalVersion, which MSBuild generates
    // from <Version> in Dtssh.csproj (the single source of truth) plus the git
    // short SHA (e.g. "0.0.0+a1b2c3d4"). See the SetGitRevision target in the csproj.
    public static readonly string Version =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    private static async Task<int> Main(string[] args)
    {
        // Make the DevTunnels SDK serialize via source-gen (NativeAOT-safe).
        DtsshJson.Install();

        if (args.Length < 1)
        {
            // When wired in as the OpenSSH DefaultShell (opt-in; see `dtssh shell
            // --help`), sshd invokes us BARE — no subcommand, no command option —
            // for an INTERACTIVE session (it only appends DefaultShellCommandOption
            // for exec/`ssh host "cmd"`). Route that to the headless shell shim so
            // the real interactive shell launches. A bare `dtssh` at a terminal
            // still prints usage. Heuristic: an SSH session sets SSH_CONNECTION.
            if (Environment.GetEnvironmentVariable("SSH_CONNECTION") is not null)
                return await ShellCommand.RunAsync([]);

            PrintUsage();
            return 2;
        }

        var cmd = args[0];
        var rest = args[1..];

        try
        {
            switch (cmd)
            {
                case "host": return await HostCommand.RunAsync(rest);
                case "service": return await ServiceCommand.RunAsync(rest);
                case "proxy": return await ProxyCommand.RunAsync(rest);
                case "discover" or "sync": return await DiscoverCommand.RunAsync(rest);
                case "login": return await LoginCommand.RunAsync(rest);
                case "list" or "ls": return await ListCommand.RunAsync(rest);
                case "remove": return await RemoveCommand.RunAsync(rest);
                case "connect": return await ConnectCommand.RunAsync(rest);
                case "config": return await ConfigCommand.RunAsync(rest);
                case "doctor": return await DoctorCommand.RunAsync(rest);
                case "shell": return await ShellCommand.RunAsync(rest);
                case "version" or "--version" or "-v":
                    Console.WriteLine($"dtssh {Version}");
                    return 0;
                case "help" or "-h" or "--help":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"dtssh: unknown command \"{cmd}\"\n");
                    PrintUsage();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dtssh: {ex.Message}");
            Log.Debugf("unhandled: {0}", ex);
            return 1;
        }
    }

    private static void PrintUsage() => Console.Error.Write(
"""
dtssh — seamless SSH over Microsoft Dev Tunnels

USAGE:
    dtssh <command> [options]

HOST COMMANDS (run on the machine you want to reach):
    host        Expose this machine for SSH over a dev tunnel; publish
                discovery metadata so logged-in clients find it automatically.
    service     Run the host as an auto-restarting user service (install,
                uninstall, start, stop, status, logs).

SETUP:
    login       Log into Microsoft Dev Tunnels (auto-downloads the devtunnel
                CLI if missing); passes flags through to `devtunnel user login`.

CLIENT COMMANDS (run on the machine you connect from):
    discover    Auto-discover dtssh hosts on your devtunnel account and wire
                up 'ssh <alias>' with no setup — the only pairing step.
    list        List the host aliases dtssh currently manages.
    remove      Remove a managed host alias (config, key, known_hosts pin).
    connect     Thin ssh wrapper: dtssh connect <alias> [ssh args...]
    proxy       OpenSSH ProxyCommand (wired up automatically by discover).

UTILITIES:
    config      Print an ssh_config block for a tunnel.
    doctor      Check that devtunnel/ssh/sshd are available.
    shell       (Windows, opt-in) Headless DefaultShell shim that suppresses the
                console window Windows pops for each non-PTY exec against a host.
    version     Print version.

EXAMPLES:
    # First-time setup (auto-downloads the devtunnel CLI, then logs you in):
    dtssh login

    # On the host:
    dtssh host --port 2222

    # Keep the host up automatically (auto-start at login + auto-restart):
    dtssh service install --port 2222

    # On a client logged into the same devtunnel account:
    dtssh discover
    ssh dt-myhost

""");
}
