using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dtssh.Infra;

internal sealed class ParentExitMonitor : IDisposable
{
    private readonly Process? parent;
    private readonly CancellationTokenSource exited = new();

    private ParentExitMonitor(Process? parent)
    {
        this.parent = parent;
        if (parent is null) return;

        parent.EnableRaisingEvents = true;
        parent.Exited += OnExited;
        if (parent.HasExited) exited.Cancel();
    }

    public CancellationToken Token => exited.Token;

    public static ParentExitMonitor Start()
    {
        if (!OperatingSystem.IsWindows()) return new ParentExitMonitor(null);

        var info = new ProcessBasicInformation();
        var status = NtQueryInformationProcess(
            Process.GetCurrentProcess().Handle,
            0,
            ref info,
            (uint)Marshal.SizeOf<ProcessBasicInformation>(),
            out _);
        if (status != 0 || info.InheritedFromUniqueProcessId == IntPtr.Zero)
            return new ParentExitMonitor(null);

        try
        {
            return new ParentExitMonitor(
                Process.GetProcessById(info.InheritedFromUniqueProcessId.ToInt32()));
        }
        catch (ArgumentException)
        {
            var monitor = new ParentExitMonitor(null);
            monitor.exited.Cancel();
            return monitor;
        }
    }

    private void OnExited(object? sender, EventArgs e) => exited.Cancel();

    public void Dispose()
    {
        if (parent is not null)
        {
            parent.Exited -= OnExited;
            parent.Dispose();
        }
        exited.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        uint processInformationLength,
        out uint returnLength);
}
