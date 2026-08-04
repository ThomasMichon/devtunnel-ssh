<p align="center">
  <img src="assets/logo.svg" alt="dtssh" width="120" height="120">
</p>

# dtssh — seamless SSH over Microsoft Dev Tunnels

`dtssh` lets you `ssh` into any machine through a
[Microsoft Dev Tunnel](https://learn.microsoft.com/azure/developer/dev-tunnels/) —
across NAT and firewalls, with **no SSH keys, passwords, or host-key prompts to
set up**. It runs a dedicated SSH server on the host (reachable only through the
tunnel) and auto-configures the client, so plain `ssh`, `scp`, `git`, and
**VS Code Remote-SSH** just work.

Both machines only need to be logged into the **same devtunnel account**.

## Install

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/bmiddha/devtunnel-ssh/main/scripts/install-release.sh | bash
```

```powershell
# Windows, or anywhere with PowerShell 7+ (also macOS/Linux)
irm https://raw.githubusercontent.com/bmiddha/devtunnel-ssh/main/scripts/install-release.ps1 | iex
```

This downloads a prebuilt binary from the latest
[GitHub Release](https://github.com/bmiddha/devtunnel-ssh/releases). You also
need OpenSSH (`ssh`; plus `sshd` on Linux/macOS hosts). On Windows, dtssh
downloads a pinned portable Win32-OpenSSH build on first host startup. The
`devtunnel` CLI is auto-downloaded on first use.

## Use it

On the **host** (the machine you want to reach):

```bash
dtssh login    # sign in once (opens a browser)
dtssh host     # start hosting
```

On the **client** (the machine you connect from):

```bash
dtssh login       # same account as the host
dtssh discover    # finds the host, sets up `ssh dt-<host>`
ssh dt-<host>     # done
```

That's it. `scp`, `git`, `rsync`, and VS Code Remote-SSH work against the same
alias.

### Run the host as a service

Keep a machine reachable without leaving a terminal open (starts at login,
auto-restarts, no admin needed):

```bash
dtssh service install     # same flags as `dtssh host`
dtssh service status      # or: logs, restart, stop, start, uninstall
```

## Commands

| Command | What it does |
| --- | --- |
| `dtssh login` | Sign in to devtunnels (flags pass through, e.g. `-d` device code). |
| `dtssh host` | Expose this machine and publish discovery info. |
| `dtssh discover` | Find hosts and wire up `ssh <alias>` (the only pairing step). |
| `dtssh service …` | Run the host as a background service (`install`/`status`/`logs`/…). |
| `dtssh list` | List configured host aliases. |
| `dtssh remove <alias>` | Remove a host alias. |
| `dtssh doctor` | Check your environment. |

Run `dtssh <command> --help` for options. Set `DTSSH_DEBUG=1` for diagnostics.

## How it works

`dtssh host` starts a hardened `sshd` bound to loopback and hosts that port over
a dev tunnel in-process via the
[Dev Tunnels SDK](https://github.com/microsoft/dev-tunnels). It publishes pairing
info (alias, host key, and an ephemeral key seed) in the tunnel's account-private
description. `dtssh discover` reads that, installs the key, pins the host key, and
adds an `ssh_config` alias whose `ProxyCommand` (`dtssh proxy`) pipes SSH straight
into the tunnel.

So `ssh <alias>` does normal SSH public-key auth over the tunnel — "no extra
auth" means dtssh provisions the key for you, not that SSH auth is disabled. The
tunnel is the only ingress, and the host key is pinned (no trust-on-first-use),
so the relay can't MITM the connection.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download); `dtssh` is a
single self-contained NativeAOT binary.

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```
