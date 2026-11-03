using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameBooster.Services;

public interface IPowerPlans
{
    Guid ActiveScheme();
    void SetScheme(Guid scheme);
}

public interface IGameProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IGameLauncher
{
    IGameProcess Start(string executable);
}

public sealed class WindowsPowerPlans : IPowerPlans
{
    public Guid ActiveScheme()
    {
        var error = PowerGetActiveScheme(0, out var pointer);
        if (error != 0) throw new Win32Exception((int)error, "Cannot read the active power plan.");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public void SetScheme(Guid scheme)
    {
        var error = PowerSetActiveScheme(0, ref scheme);
        if (error != 0) throw new Win32Exception((int)error, "Windows rejected the power-plan change.");
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}

public sealed class WindowsGameLauncher : IGameLauncher
{
    public IGameProcess Start(string executable)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable),
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start the game.");
        return new GameProcess(process);
    }

    private sealed class GameProcess(Process process) : IGameProcess
    {
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void Dispose() => process.Dispose();
    }
}

public sealed class SystemMonitor
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    public MonitorSample Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot sample system CPU usage.");
        var total = kernel + user;
        var elapsed = total - _lastTotal;
        var idleElapsed = idle - _lastIdle;
        var cpu = _lastTotal == 0 || elapsed == 0 ? 0 : (float)Math.Clamp(100d * (elapsed - Math.Min(elapsed, idleElapsed)) / elapsed, 0, 100);
        _lastIdle = idle;
        _lastTotal = total;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot sample physical memory.");
        return new(cpu, memory.TotalPhysical - memory.AvailablePhysical, memory.TotalPhysical);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
