using System.Net;
using System.Net.Sockets;
using Microsoft.DevTunnels.Connections;
using Dtssh.Auth;
using Dtssh.Connections;
using Dtssh.Infra;

namespace Dtssh.Commands;

// `dtssh proxy <tunnel-id> [--port N] [--direct host:port]`
// OpenSSH ProxyCommand: bridges ssh's stdin/stdout to the remote sshd over the
// dev tunnel, in-process via the DevTunnels SDK (no `devtunnel connect` subprocess).
// The connect token is minted on the fly from the client's own devtunnel login.
internal static class ProxyCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var f = Flags.Parse(args);
        var tunnelId = f.Arg(0);
        var port = f.Int("port", 2222);
        var direct = f.Str("direct", Environment.GetEnvironmentVariable("DTSSH_DIRECT") ?? "");

        if (!string.IsNullOrEmpty(direct))
        {
            Log.Debugf("proxy: direct bridge to {0}", direct);
            return await DirectBridgeAsync(direct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(tunnelId))
        {
            Console.Error.WriteLine("usage: dtssh proxy <tunnel-id> [--port N]");
            return 2;
        }

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        return await TunnelBridgeAsync(tunnelId, port, "", stdin, stdout).ConfigureAwait(false);
    }

    private static async Task<int> TunnelBridgeAsync(
        string tunnelId, int port, string token, Stream stdin, Stream stdout)
    {
        var ct = CancellationToken.None;

        // Self-heal the pinned host key from the authoritative account metadata
        // before connecting, so a host that rotated its key still connects. Each
        // ssh spawns a fresh proxy, so throttle via the on-disk TTL stamp
        // (cold: false): the first connection (or first after the TTL expires)
        // does the metadata fetch, later ones skip it — no per-connection network
        // round-trip. Best-effort — never blocks the connection.
        await Discovery.HostKey.ReconcileAsync(tunnelId, cold: false, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(token))
            token = await DevtunnelCli.IssueTokenAsync(tunnelId, "connect", ct).ConfigureAwait(false);

        var trace = Relay.Trace();
        var mgmt = Relay.ManagementClient();
        var tunnel = await Relay.FetchTunnelAsync(mgmt, tunnelId, token, "connect", ct).ConfigureAwait(false);

        await using var client = new TunnelRelayTunnelClient(mgmt, trace)
        {
            // We stream the port directly into ssh's stdio; no local TCP listener.
            AcceptLocalConnectionsForForwardedPorts = false,
        };
        // Re-mint the connect token from the CLI when it nears expiry so long
        // interactive sessions survive token rotation without dropping.
        client.RefreshingTunnelAccessToken += (_, e) => Relay.OnRefreshToken(e, tunnelId);

        var options = new TunnelConnectionOptions { EnableReconnect = true, EnableRetry = true };
        Log.Debugf("proxy: connecting tunnel {0} port {1}", tunnelId, port);
        await client.ConnectAsync(tunnel, options, ct).ConfigureAwait(false);
        await client.WaitForForwardedPortAsync(port, ct).ConfigureAwait(false);

        var stream = await client.ConnectToForwardedPortAsync(port, ct).ConfigureAwait(false)
            ?? throw new DtsshException($"forwarded port {port} not available on tunnel {tunnelId}");

        Log.Debugf("proxy: bridging stdio to forwarded port {0}", port);
        await using (stream)
        {
            await PumpAsync(stdin, stdout, stream).ConfigureAwait(false);
        }
        return 0;
    }

    private static async Task<int> DirectBridgeAsync(string hostPort)
    {
        var (host, port) = ParseHostPort(hostPort);
        using var tcp = new TcpClient();
        await ConnectWithRetryAsync(tcp, host, port, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        using var net = tcp.GetStream();
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        await PumpAsync(stdin, stdout, net).ConfigureAwait(false);
        return 0;
    }

    // Copies stdin->remote and remote->stdout concurrently. Once either side
    // closes, stop the other pump and close the forwarded stream before the
    // relay client is disposed. Without this bounded teardown, completed
    // ProxyCommand processes can leave their host-side forwarded sockets alive.
    private static async Task PumpAsync(Stream stdin, Stream stdout, Stream remote)
    {
        using var cts = new CancellationTokenSource();
        var up = stdin.CopyToAsync(remote, cts.Token);
        var down = remote.CopyToAsync(stdout, cts.Token);
        var completed = await Task.WhenAny(up, down).ConfigureAwait(false);

        try
        {
            await completed.ConfigureAwait(false);
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await remote.DisposeAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(up, down)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Expected for the pump that did not finish first.
            }
            catch (IOException)
            {
                // Expected when stream disposal releases a blocked pipe read/write.
            }
            catch (ObjectDisposedException)
            {
                // Expected when stream disposal wins the cancellation race.
            }
            catch (TimeoutException)
            {
                // Some console handles do not observe cancellation; relay/client
                // disposal below is the final bounded teardown.
            }
        }
    }

    private static async Task ConnectWithRetryAsync(TcpClient tcp, string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try { await tcp.ConnectAsync(host, port).ConfigureAwait(false); return; }
            catch (SocketException ex) { last = ex; await Task.Delay(200).ConfigureAwait(false); }
        }
        throw new DtsshException($"connect to {host}:{port}: {last?.Message}");
    }

    private static (string host, int port) ParseHostPort(string s)
    {
        var i = s.LastIndexOf(':');
        if (i < 0 || !int.TryParse(s[(i + 1)..], out var p))
            throw new DtsshException($"invalid --direct address {s} (want host:port)");
        var host = s[..i].Trim('[', ']');
        return (host, p);
    }
}
