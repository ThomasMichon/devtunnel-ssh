using System.Diagnostics;
using System.Text;

namespace Dtssh.Infra;

// Result of running an external process.
internal readonly record struct ProcResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string StdoutTrim => Stdout.Trim();
    public string StderrTrim => Stderr.Trim();
}

internal static class Proc
{
    // Runs an executable, capturing stdout/stderr. Never throws on non-zero exit.
    public static async Task<ProcResult> RunAsync(
        string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = OperatingSystem.IsWindows(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Log.Debugf("exec: {0} {1}", file, string.Join(' ', args));
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var eb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) eb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(p);
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return new ProcResult(p.ExitCode, sb.ToString(), eb.ToString());
    }

    // Runs an executable with inherited stdio (for interactive flows like login).
    public static async Task<int> RunInteractiveAsync(
        string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.Start();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(p);
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        return p.ExitCode;
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the HasExited check and Kill.
        }
    }

    // Spawns a long-running child process, streaming its stderr to our stderr with
    // a prefix (used for the dedicated sshd). The returned Process is owned by the
    // caller, which must kill it on shutdown.
    public static Process Spawn(
        string file,
        IReadOnlyList<string> args,
        string stderrPrefix,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = OperatingSystem.IsWindows(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        Log.Debugf("spawn: {0} {1}", file, string.Join(' ', args));
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Console.Error.WriteLine(stderrPrefix + e.Data);
        };
        p.Start();
        p.BeginErrorReadLine();
        return p;
    }

    // Resolves an executable name against PATH (adding platform extensions on
    // Windows), mirroring exec.LookPath. Returns null if not found.
    public static string? Which(string name)
    {
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains('/'))
            return File.Exists(name) ? name : null;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };

        foreach (var dir in dirs)
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
