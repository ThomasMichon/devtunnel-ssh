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
//   dtssh shell install          → wire it up as the OpenSSH DefaultShell (admin)
//   dtssh shell uninstall        → restore the previous DefaultShell (admin)
//   dtssh shell status           → show current wiring
//
// It is OPT-IN and off by default: `install` writes the machine-global
// HKLM\SOFTWARE\OpenSSH DefaultShell (which also affects the system sshd on :22),
// needs elevation, and only benefits non-PTY exec.
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
                case "install": return await InstallAsync(clear: false);
                case "uninstall": return await InstallAsync(clear: true);
                case "status": return await StatusAsync();
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

    // sshd invokes `<DefaultShell> <DefaultShellCommandOption> "<command>"`. We wire
    // the option to "shell", so we arrive as `dtssh shell "<command>"` (command in
    // args[0]). Also accept an explicit `-c <command>` for manual invocation.
    private static string ExtractCommand(string[] args)
    {
        if (args.Length == 0) return "";
        if (args[0] is "-c" or "-Command" or "/c" or "/C")
            return args.Length > 1 ? string.Join(' ', args[1..]) : "";
        return string.Join(' ', args);
    }

    // The shell to forward to: the DefaultShell captured at `install` time (so the
    // shim is transparent to whatever the machine had), falling back to pwsh then
    // cmd on Windows, or $SHELL/sh elsewhere. Returns (shellPath, commandOption).
    private static (string shell, string option) ResolveRealShell()
    {
        var saved = ReadSavedPrevious();
        if (saved is { } prev && !string.IsNullOrEmpty(prev.shell) &&
            !prev.shell.Equals(SelfPath(), StringComparison.OrdinalIgnoreCase))
        {
            return (prev.shell, string.IsNullOrEmpty(prev.option) ? DefaultOptionFor(prev.shell) : prev.option);
        }

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

    private static string DefaultOptionFor(string shell)
    {
        var name = Path.GetFileNameWithoutExtension(shell).ToLowerInvariant();
        return name is "cmd" ? "/c" : "-c";
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

    // ── install / uninstall / status ─────────────────────────────────────────

    private static async Task<int> InstallAsync(bool clear)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("dtssh shell install: Windows-only (no console-window problem elsewhere).");
            return 1;
        }

        if (clear)
        {
            var prev = ReadSavedPrevious();
            if (prev is { } p)
            {
                var rc = await ApplyDefaultShellAsync(p.shell, p.option).ConfigureAwait(false);
                if (rc != 0) return rc;
                DeleteSavedPrevious();
                Console.Error.WriteLine("dtssh shell: restored the previous OpenSSH DefaultShell.");
            }
            else
            {
                var rc = await ClearValueAsync(DefaultShellValue).ConfigureAwait(false);
                _ = await ClearValueAsync(DefaultShellOptionValue).ConfigureAwait(false);
                if (rc != 0) return rc;
                Console.Error.WriteLine("dtssh shell: cleared the OpenSSH DefaultShell (back to the OpenSSH default).");
            }
            return 0;
        }

        var self = SelfPath();
        var (curShell, curOption) = await ReadCurrentAsync().ConfigureAwait(false);
        if (curShell.Equals(self, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("dtssh shell: already installed as the OpenSSH DefaultShell.");
            return 0;
        }

        // Capture the current DefaultShell so `uninstall` can restore it exactly.
        SaveSavedPrevious(curShell, curOption);

        var apply = await ApplyDefaultShellAsync(self, "shell").ConfigureAwait(false);
        if (apply != 0) { DeleteSavedPrevious(); return apply; }

        Console.Error.WriteLine($"dtssh shell: installed as the OpenSSH DefaultShell ({self} shell).");
        Console.Error.WriteLine("dtssh shell: note — this is machine-global (also affects the system sshd on :22); no sshd restart is needed.");
        return 0;
    }

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
        var prev = ReadSavedPrevious();
        if (prev is { } p)
            Console.WriteLine($"saved previous shell      = {(string.IsNullOrEmpty(p.shell) ? "(was unset)" : p.shell)} {p.option}");
        return 0;
    }

    // ── registry via reg.exe (AOT-trivial; no Microsoft.Win32.Registry dep) ──

    private static async Task<int> ApplyDefaultShellAsync(string shell, string option)
    {
        if (string.IsNullOrEmpty(shell))
        {
            var rc = await ClearValueAsync(DefaultShellValue).ConfigureAwait(false);
            _ = await ClearValueAsync(DefaultShellOptionValue).ConfigureAwait(false);
            return rc;
        }
        var s = await SetValueAsync(DefaultShellValue, shell).ConfigureAwait(false);
        if (s != 0) return s;
        return string.IsNullOrEmpty(option)
            ? await ClearValueAsync(DefaultShellOptionValue).ConfigureAwait(false)
            : await SetValueAsync(DefaultShellOptionValue, option).ConfigureAwait(false);
    }

    private static async Task<int> SetValueAsync(string name, string data)
    {
        var r = await Proc.RunAsync("reg.exe",
            new[] { "add", OpenSshKey, "/v", name, "/t", "REG_SZ", "/d", data, "/f" }).ConfigureAwait(false);
        if (r.Ok) return 0;
        return ReportRegFailure(r);
    }

    private static async Task<int> ClearValueAsync(string name)
    {
        var r = await Proc.RunAsync("reg.exe",
            new[] { "delete", OpenSshKey, "/v", name, "/f" }).ConfigureAwait(false);
        // Deleting an absent value returns non-zero; treat "not found" as success.
        if (r.Ok || r.Stderr.Contains("unable to find", StringComparison.OrdinalIgnoreCase)) return 0;
        return ReportRegFailure(r);
    }

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

    private static int ReportRegFailure(ProcResult r)
    {
        var msg = r.StderrTrim.Length > 0 ? r.StderrTrim : r.StdoutTrim;
        if (msg.Contains("denied", StringComparison.OrdinalIgnoreCase) || msg.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("dtssh shell: writing HKLM\\SOFTWARE\\OpenSSH requires an elevated (Administrator) shell.");
            return 1;
        }
        Console.Error.WriteLine($"dtssh shell: registry update failed: {msg}");
        return 1;
    }

    // ── previous-DefaultShell state (so uninstall restores it exactly) ───────

    private static string SavedPreviousPath() => Path.Combine(Paths.HostDir(), "default-shell.prev");

    private static void SaveSavedPrevious(string shell, string option)
    {
        try
        {
            Paths.EnsureDir(Paths.HostDir());
            File.WriteAllText(SavedPreviousPath(), shell + "\n" + option + "\n");
        }
        catch (Exception e) { Console.Error.WriteLine($"dtssh shell: warning: could not record previous shell: {e.Message}"); }
    }

    private static (string shell, string option)? ReadSavedPrevious()
    {
        try
        {
            var p = SavedPreviousPath();
            if (!File.Exists(p)) return null;
            var lines = File.ReadAllLines(p);
            return (lines.Length > 0 ? lines[0].Trim() : "", lines.Length > 1 ? lines[1].Trim() : "");
        }
        catch { return null; }
    }

    private static void DeleteSavedPrevious()
    {
        try { File.Delete(SavedPreviousPath()); } catch { /* best-effort */ }
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
    dtssh shell install      wire dtssh in as the OpenSSH DefaultShell (needs admin)
    dtssh shell uninstall    restore the previous DefaultShell (needs admin)
    dtssh shell status       show the current DefaultShell wiring
    dtssh shell -c "<cmd>"   (shim) hide console, forward "<cmd>" to the real shell

`install` writes HKLM\SOFTWARE\OpenSSH\DefaultShell = "<dtssh> shell" and
DefaultShellCommandOption = "shell". This is machine-global (it also affects the
system sshd on :22) and only benefits non-PTY exec, so it is opt-in.

""");
}
