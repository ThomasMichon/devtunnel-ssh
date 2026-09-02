using System.Runtime.InteropServices;
using System.Text;
using Dtssh.Auth;
using Dtssh.Infra;

namespace Dtssh.Service;

// Registers dtssh as a per-user, auto-restarting background service so a host
// machine keeps itself reachable over a dev tunnel without a human running
// `dtssh host` in a terminal. Each platform uses its native per-user supervisor,
// all configured to restart on failure (auto-recovery) and start at login:
//
//   Linux    systemd user unit    (~/.config/systemd/user/dtssh-host.service)
//   macOS    launchd LaunchAgent  (~/Library/LaunchAgents/com.bmiddha.dtssh-host.plist)
//   Windows  Task Scheduler task  (\dtssh-host, logon trigger, restart-on-failure)
//
// No administrator privileges are required. Ported from the Go internal/service.
internal sealed record ServiceConfig(string Exe, IReadOnlyList<string> HostArgs, IReadOnlyList<string> Env);

internal interface IServiceManager
{
    void Install(ServiceConfig cfg);
    void Uninstall();
    void Start();
    void Stop();
    void Status();
    void Logs();
    string UnitPath();
}

internal static class ServiceManager
{
    public const string Name = "dtssh-host";

    public static IServiceManager New()
    {
        if (OperatingSystem.IsLinux()) return new SystemdManager();
        if (OperatingSystem.IsMacOS()) return new LaunchdManager();
        if (OperatingSystem.IsWindows()) return new SchtasksManager();
        throw new DtsshException("dtssh service is not supported on this platform");
    }

    // The file the service writes stdout/stderr to on platforms that use file
    // redirection (macOS, Windows). systemd uses journald instead.
    public static string LogPath() => Path.Combine(Paths.LogsDir(), Name + ".log");

    // Captures the current interactive PATH so the installed service can locate
    // the devtunnel/ssh tooling that was resolvable at install time.
    public static IReadOnlyList<string> DefaultEnv()
    {
        var env = new List<string> { "DTSSH_SERVICE=1" };
        void Add(string k) { var v = Environment.GetEnvironmentVariable(k); if (!string.IsNullOrEmpty(v)) env.Add($"{k}={v}"); }
        Add("PATH");
        Add("DTSSH_HOME");
        Add("DTSSH_DEVTUNNEL");
        return env;
    }
}

// Shared process helpers for the service managers.
internal static class Svc
{
    // Runs a command, streaming output to the user's stderr; throws on failure.
    public static void Run(string name, params string[] args)
    {
        var r = Proc.RunAsync(name, args).GetAwaiter().GetResult();
        if (r.Stdout.Length > 0) Console.Error.Write(r.Stdout);
        if (r.Stderr.Length > 0) Console.Error.Write(r.Stderr);
        if (!r.Ok) throw new DtsshException($"{name} {string.Join(' ', args)}: exit {r.ExitCode}");
    }

    // Runs a best-effort command, returning success and discarding output.
    public static bool TryRun(string name, params string[] args)
    {
        try { return Proc.RunAsync(name, args).GetAwaiter().GetResult().Ok; }
        catch { return false; }
    }

    public static string XmlEsc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");

    public static (string key, string value) SplitEnv(string e)
    {
        var i = e.IndexOf('=');
        return i >= 0 ? (e[..i], e[(i + 1)..]) : (e, "");
    }

    // Prints the tail of a log file, or a hint if it does not exist.
    public static void TailFile(string path)
    {
        if (!File.Exists(path)) throw new DtsshException($"no log at {path} yet");
        // The running service keeps the log open for writing, so share read+write
        // to avoid a sharing violation on Windows ("used by another process").
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            const int max = 16 * 1024;
            if (fs.Length > max) fs.Seek(-max, SeekOrigin.End);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        Console.Error.WriteLine($"==> {path} <==");
        Console.Error.Write(Encoding.UTF8.GetString(bytes));
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n') Console.Error.WriteLine();
    }

    public static string HomeDir() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

// --- Linux: systemd user unit ---
internal sealed class SystemdManager : IServiceManager
{
    private static string Unit => ServiceManager.Name + ".service";

    public string UnitPath() =>
        Path.Combine(Svc.HomeDir(), ".config", "systemd", "user", Unit);

    public void Install(ServiceConfig cfg)
    {
        if (Proc.Which("systemctl") is null)
            throw new DtsshException("systemctl not found; this Linux system does not use systemd (a user service cannot be installed)");
        var path = UnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Render(cfg));
        Svc.Run("systemctl", "--user", "daemon-reload");
        if (!Svc.TryRun("loginctl", "enable-linger"))
            Console.Error.WriteLine("dtssh: note: could not enable lingering; the service runs only while you are logged in (run `loginctl enable-linger $USER` to change this).");
        Svc.Run("systemctl", "--user", "enable", "--now", Unit);
    }

    private static string Render(ServiceConfig cfg)
    {
        var b = new StringBuilder();
        b.Append("[Unit]\n");
        b.Append("Description=dtssh — seamless SSH over Microsoft Dev Tunnels host\n");
        b.Append("After=network-online.target\n");
        b.Append("Wants=network-online.target\n\n");
        b.Append("[Service]\n");
        b.Append("Type=simple\n");
        var argv = new List<string> { cfg.Exe, "host" };
        argv.AddRange(cfg.HostArgs);
        b.Append($"ExecStart={QuoteArgs(argv)}\n");
        b.Append("Restart=always\n");
        b.Append("RestartSec=5\n");
        foreach (var e in cfg.Env) b.Append($"Environment={e}\n");
        b.Append("\n[Install]\n");
        b.Append("WantedBy=default.target\n");
        return b.ToString();
    }

    public void Uninstall()
    {
        Svc.TryRun("systemctl", "--user", "disable", "--now", Unit);
        try { if (File.Exists(UnitPath())) File.Delete(UnitPath()); } catch (Exception e) { throw new DtsshException(e.Message); }
        Svc.Run("systemctl", "--user", "daemon-reload");
    }

    public void Start() => Svc.Run("systemctl", "--user", "start", Unit);
    public void Stop() => Svc.Run("systemctl", "--user", "stop", Unit);
    public void Status() => Svc.Run("systemctl", "--user", "--no-pager", "status", Unit);
    public void Logs() => Svc.Run("journalctl", "--user", "-u", Unit, "-n", "200", "--no-pager");

    private static string QuoteArgs(IReadOnlyList<string> argv) =>
        string.Join(' ', argv.Select(a =>
            a.Any(c => c is ' ' or '\t') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
}

// --- macOS: launchd LaunchAgent ---
internal sealed class LaunchdManager : IServiceManager
{
    private const string Label = "com.bmiddha.dtssh-host";

    [DllImport("libc", SetLastError = false)]
    private static extern uint getuid();

    public string UnitPath() =>
        Path.Combine(Svc.HomeDir(), "Library", "LaunchAgents", Label + ".plist");

    private static string Domain() => "gui/" + getuid();
    private static string Target() => Domain() + "/" + Label;

    public void Install(ServiceConfig cfg)
    {
        var path = UnitPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ServiceManager.LogPath())!);
        File.WriteAllText(path, Render(cfg));
        Svc.TryRun("launchctl", "bootout", Target());
        if (!Svc.TryRun("launchctl", "bootstrap", Domain(), path))
            Svc.Run("launchctl", "load", "-w", path);
        Svc.TryRun("launchctl", "enable", Target());
        Svc.TryRun("launchctl", "kickstart", "-k", Target());
    }

    private static string Render(ServiceConfig cfg)
    {
        var argv = new List<string> { cfg.Exe, "host" };
        argv.AddRange(cfg.HostArgs);
        var b = new StringBuilder();
        b.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        b.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        b.Append("<plist version=\"1.0\">\n<dict>\n");
        b.Append($"  <key>Label</key>\n  <string>{Svc.XmlEsc(Label)}</string>\n");
        b.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var a in argv) b.Append($"    <string>{Svc.XmlEsc(a)}</string>\n");
        b.Append("  </array>\n");
        b.Append("  <key>RunAtLoad</key>\n  <true/>\n");
        b.Append("  <key>KeepAlive</key>\n  <true/>\n");
        b.Append("  <key>ThrottleInterval</key>\n  <integer>5</integer>\n");
        b.Append("  <key>ProcessType</key>\n  <string>Background</string>\n");
        b.Append($"  <key>StandardOutPath</key>\n  <string>{Svc.XmlEsc(ServiceManager.LogPath())}</string>\n");
        b.Append($"  <key>StandardErrorPath</key>\n  <string>{Svc.XmlEsc(ServiceManager.LogPath())}</string>\n");
        if (cfg.Env.Count > 0)
        {
            b.Append("  <key>EnvironmentVariables</key>\n  <dict>\n");
            foreach (var e in cfg.Env)
            {
                var (k, v) = Svc.SplitEnv(e);
                b.Append($"    <key>{Svc.XmlEsc(k)}</key>\n    <string>{Svc.XmlEsc(v)}</string>\n");
            }
            b.Append("  </dict>\n");
        }
        b.Append("</dict>\n</plist>\n");
        return b.ToString();
    }

    public void Uninstall()
    {
        if (!Svc.TryRun("launchctl", "bootout", Target()))
            Svc.TryRun("launchctl", "unload", "-w", UnitPath());
        try { if (File.Exists(UnitPath())) File.Delete(UnitPath()); } catch (Exception e) { throw new DtsshException(e.Message); }
    }

    public void Start() => Svc.Run("launchctl", "kickstart", "-k", Target());
    public void Stop() => Svc.Run("launchctl", "kill", "SIGTERM", Target());
    public void Status() => Svc.Run("launchctl", "print", Target());
    public void Logs() => Svc.TailFile(ServiceManager.LogPath());
}

// --- Windows: Task Scheduler ---
internal sealed class SchtasksManager : IServiceManager
{
    private const string TaskName = "\\dtssh-host";

    public string UnitPath() => Path.Combine(Path.GetTempPath(), ServiceManager.Name + ".xml");

    public void Install(ServiceConfig cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServiceManager.LogPath())!);
        var xmlPath = UnitPath();
        File.WriteAllText(xmlPath, Render(cfg));
        try
        {
            Svc.Run("schtasks", "/Create", "/TN", TaskName, "/XML", xmlPath, "/F");
            Svc.Run("schtasks", "/Run", "/TN", TaskName);
        }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    private static string Render(ServiceConfig cfg)
    {
        var argv = new List<string> { cfg.Exe, "host" };
        argv.AddRange(cfg.HostArgs);
        var command = argv[0];
        var arguments = string.Join(' ', argv.Skip(1).Select(QuoteIfSpace));
        var logFile = ServiceManager.LogPath();
        var wrapped = $"/d /s /c \"{QuoteIfSpace(command)} {arguments} >> \"{logFile}\" 2>&1\"";
        var conhost = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "conhost.exe");

        var b = new StringBuilder();
        b.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n");
        b.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n");
        b.Append("  <RegistrationInfo>\n");
        b.Append("    <Description>dtssh — seamless SSH over Microsoft Dev Tunnels host</Description>\n");
        b.Append("  </RegistrationInfo>\n");
        b.Append("  <Triggers>\n");
        b.Append("    <LogonTrigger>\n      <Enabled>true</Enabled>\n    </LogonTrigger>\n");
        b.Append("  </Triggers>\n");
        b.Append("  <Principals>\n");
        b.Append("    <Principal id=\"Author\">\n");
        b.Append("      <LogonType>InteractiveToken</LogonType>\n");
        b.Append("      <RunLevel>LeastPrivilege</RunLevel>\n");
        b.Append("    </Principal>\n");
        b.Append("  </Principals>\n");
        b.Append("  <Settings>\n");
        b.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n");
        b.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n");
        b.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n");
        b.Append("    <AllowHardTerminate>true</AllowHardTerminate>\n");
        b.Append("    <StartWhenAvailable>true</StartWhenAvailable>\n");
        b.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n");
        b.Append("    <Enabled>true</Enabled>\n");
        b.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n");
        b.Append("    <RestartOnFailure>\n");
        b.Append("      <Interval>PT1M</Interval>\n");
        b.Append("      <Count>999</Count>\n");
        b.Append("    </RestartOnFailure>\n");
        b.Append("  </Settings>\n");
        b.Append("  <Actions Context=\"Author\">\n");
        b.Append("    <Exec>\n");
        b.Append($"      <Command>{Svc.XmlEsc(conhost)}</Command>\n");
        b.Append($"      <Arguments>{Svc.XmlEsc("--headless cmd.exe " + wrapped)}</Arguments>\n");
        b.Append("    </Exec>\n");
        b.Append("  </Actions>\n");
        b.Append("</Task>\n");
        return b.ToString();
    }

    public void Uninstall()
    {
        Svc.TryRun("schtasks", "/End", "/TN", TaskName);
        Svc.Run("schtasks", "/Delete", "/TN", TaskName, "/F");
    }

    public void Start() => Svc.Run("schtasks", "/Run", "/TN", TaskName);
    public void Stop() => Svc.Run("schtasks", "/End", "/TN", TaskName);
    public void Status() => Svc.Run("schtasks", "/Query", "/TN", TaskName, "/V", "/FO", "LIST");
    public void Logs() => Svc.TailFile(ServiceManager.LogPath());

    private static string QuoteIfSpace(string s) => s.Any(c => c is ' ' or '\t') ? "\"" + s + "\"" : s;
}
