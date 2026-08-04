using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dtssh.Infra;

namespace Dtssh.Commands;

// `dtssh shell` — an opt-in, transparent "headless DefaultShell" shim for Windows.
//
// WHY. `dtssh host` runs its dedicated sshd as the interactive user, in the
// interactive session. On Windows a NON-PTY exec request (`ssh <alias> "<cmd>"`)
// makes sshd spawn the configured DefaultShell as a child with its OWN console,
// and because that child lives in the interactive session the console window is
// drawn on the user's desktop. Workflows that run many non-interactive execs
// against a host therefore get a stream of console windows. Interactive/PTY
// sessions (a plain `ssh <alias>` shell, VS Code Remote-SSH, …) are unaffected —
// they run over a ConPTY with no desktop window.
//
// Neither the DefaultShell choice (cmd vs pwsh) nor `-WindowStyle Hidden` on the
// inner command hides it: the visible console belongs to the OUTER exec child.
// A PTY avoids it but corrupts machine-readable stdout (ConPTY VT + line-wrap).
//
// WHAT. Set as the sshd DefaultShell, this command hides its own console as its
// first act, then transparently forwards the command to the real shell with
// inherited stdio and returns its exit code. Interactive sessions still work
// (the console is a ConPTY, so the hide is a harmless no-op).
//
//   dtssh shell -c "<command>"   → hide console, run "<real-shell> -c <command>"
//   dtssh shell                  → hide console, run the real shell interactively
//   dtssh shell status           → show the current DefaultShell wiring (read-only)
//
// WIRING IS THE OPERATOR'S STEP. `DefaultShell` is a machine-global,
// registry-only knob (Win32 OpenSSH reads HKLM\SOFTWARE\OpenSSH\DefaultShell —
// there is no sshd_config/authorized_keys equivalent), it also affects the
// system sshd on :22, and it needs elevation. dtssh therefore does NOT write it:
// wiring the shim in is a one-time operator action (see `dtssh shell --help`),
// and `status` only reads the current values. Only non-PTY exec benefits, so the
// whole thing is opt-in.
internal static class ShellCommand
{
    private const string OpenSshKey = @"HKLM\SOFTWARE\OpenSSH";
    private const string DefaultShellValue = "DefaultShell";
    private const string DefaultShellOptionValue = "DefaultShellCommandOption";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "-h" or "--help" or "help": PrintUsage(); return 0;
                case "status": return await StatusAsync().ConfigureAwait(false);
            }
        }

        // Otherwise: shim mode — hide our console, forward to the real shell.
        HideConsoleIfWindows();

        var command = ExtractCommand(args);
        var (shell, option) = ResolveRealShell();

        var forwardArgs = new List<string>();
        if (!string.IsNullOrEmpty(command))
        {
            if (!string.IsNullOrEmpty(option)) forwardArgs.Add(option);
            forwardArgs.Add(command);
        }

        try
        {
            return await Proc.RunInteractiveAsync(shell, forwardArgs).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"dtssh shell: failed to launch \"{shell}\": {e.Message}");
            return 127;
        }
    }

    // sshd invokes `<DefaultShell> <DefaultShellCommandOption> "<command>"`. With
    // the option wired to "shell", we arrive as `dtssh shell "<command>"` (command
    // in args[0]). Also accept an explicit `-c <command>` for manual invocation.
    private static string ExtractCommand(string[] args)
    {
        if (args.Length == 0) return "";
        if (args[0] is "-c" or "-Command" or "/c" or "/C")
            return args.Length > 1 ? string.Join(' ', args[1..]) : "";
        return string.Join(' ', args);
    }

    // The shell to forward to: pwsh (then powershell, then cmd) on Windows, or
    // $SHELL/sh elsewhere. Returns (shellPath, commandOption).
    private static (string shell, string option) ResolveRealShell()
    {
        if (OperatingSystem.IsWindows())
        {
            var pwsh = Proc.Which("pwsh") ?? Proc.Which("powershell");
            if (pwsh is not null) return (pwsh, "-c");
            var comspec = Environment.GetEnvironmentVariable("ComSpec");
            return (string.IsNullOrEmpty(comspec) ? "cmd.exe" : comspec, "/c");
        }

        var sh = Environment.GetEnvironmentVariable("SHELL");
        return (string.IsNullOrEmpty(sh) ? "/bin/sh" : sh, "-c");
    }

    // ── Console hiding (Windows only) ────────────────────────────────────────

    private static void HideConsoleIfWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var h = Native.GetConsoleWindow();
            if (h != IntPtr.Zero) Native.ShowWindow(h, Native.SW_HIDE);
        }
        catch { /* best-effort: never let hiding break the forward */ }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        public const int SW_HIDE = 0;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    // ── status (read-only) ───────────────────────────────────────────────────

    private static async Task<int> StatusAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("dtssh shell: Windows-only; nothing to report on this platform.");
            return 0;
        }
        var (shell, option) = await ReadCurrentAsync().ConfigureAwait(false);
        var self = SelfPath();
        var active = shell.Equals(self, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"DefaultShell              = {(string.IsNullOrEmpty(shell) ? "(unset — OpenSSH default)" : shell)}");
        Console.WriteLine($"DefaultShellCommandOption = {(string.IsNullOrEmpty(option) ? "(unset)" : option)}");
        Console.WriteLine($"dtssh headless shim       = {(active ? "ACTIVE" : "not active")}");
        return 0;
    }

    // ── registry reads via reg.exe (AOT-trivial; no Microsoft.Win32.Registry dep) ──

    private static async Task<(string shell, string option)> ReadCurrentAsync()
        => (await QueryValueAsync(DefaultShellValue).ConfigureAwait(false),
            await QueryValueAsync(DefaultShellOptionValue).ConfigureAwait(false));

    private static async Task<string> QueryValueAsync(string name)
    {
        var r = await Proc.RunAsync("reg.exe",
            new[] { "query", OpenSshKey, "/v", name }).ConfigureAwait(false);
        if (!r.Ok) return "";
        // reg query prints:  "    DefaultShell    REG_SZ    C:\...\pwsh.exe"
        foreach (var raw in r.Stdout.Split('\n'))
        {
            var line = raw.Trim();
            var idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
            if (line.StartsWith(name, StringComparison.OrdinalIgnoreCase) && idx >= 0)
                return line[(idx + "REG_SZ".Length)..].Trim();
        }
        return "";
    }

    // Absolute path to this dtssh executable (what sshd will invoke as the shell).
    private static string SelfPath() => Environment.ProcessPath ?? "dtssh";

    private static void PrintUsage() => Console.Error.Write(
"""
dtssh shell — opt-in headless DefaultShell shim (Windows)

Suppresses the console window that Windows OpenSSH pops for each NON-PTY exec
(`ssh <alias> "<cmd>"`) when the dtssh host's sshd runs in the interactive
session. Interactive/PTY sessions (a plain shell, VS Code Remote-SSH) are
unaffected.

USAGE:
    dtssh shell status       show the current DefaultShell wiring (read-only)
    dtssh shell -c "<cmd>"   (shim) hide console, forward "<cmd>" to the real shell

WIRING (one-time, operator-run, elevated). dtssh does not write the registry.
Point the OpenSSH DefaultShell at this dtssh, with the command option "shell",
from an elevated shell:

    reg add "HKLM\SOFTWARE\OpenSSH" /v DefaultShell ^
        /t REG_SZ /d "<full-path-to-dtssh.exe>" /f
    reg add "HKLM\SOFTWARE\OpenSSH" /v DefaultShellCommandOption ^
        /t REG_SZ /d "shell" /f

This is machine-global (it also affects the system sshd on :22) and only benefits
non-PTY exec, so it is opt-in. To undo, restore DefaultShell to its previous value
(or delete the value to fall back to the OpenSSH default). `dtssh shell status`
reports the current wiring; no sshd restart is needed after changing it.

""");
}
