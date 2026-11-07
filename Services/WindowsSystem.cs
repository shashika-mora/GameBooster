using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameBooster.Services;

public interface IPowerPlans
{
    Guid ActiveScheme();
    void SetScheme(Guid scheme);
    IReadOnlyList<PowerPlanOption> AvailableSchemes() => [];
}

public interface IGameProcess : IDisposable
{
    ProcessIdentity Identity { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IGameLauncher
{
    IGameProcess Start(string executable);
}

public sealed class WindowsPowerPlans : IPowerPlans
{
    public IReadOnlyList<PowerPlanOption> AvailableSchemes()
    {
        var result = new List<PowerPlanOption>();
        for (uint index = 0; index < 256; index++)
        {
            uint size = 16;
            var buffer = new byte[size];
            var error = PowerEnumerate(0, 0, 0, 16, index, buffer, ref size);
            if (error == 259) break;
            if (error != 0) throw new Win32Exception((int)error, "Cannot list power plans.");
            var scheme = new Guid(buffer);
            uint nameSize = 0;
            error = PowerReadFriendlyName(0, ref scheme, 0, 0, null, ref nameSize);
            if ((error != 0 && error != 234) || nameSize == 0 || nameSize > 65536)
                throw new Win32Exception((int)error, "Cannot read a power plan name.");
            var name = new byte[nameSize];
            error = PowerReadFriendlyName(0, ref scheme, 0, 0, name, ref nameSize);
            if (error != 0) throw new Win32Exception((int)error, "Cannot read a power plan name.");
            result.Add(new(scheme.ToString("D"), Encoding.Unicode.GetString(name).TrimEnd('\0')));
        }
        return result;
    }
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
    [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(nint root, nint scheme, nint subgroup, uint access, uint index, [Out] byte[] buffer, ref uint size);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(nint root, ref Guid scheme, nint subgroup, nint setting, [Out] byte[]? buffer, ref uint size);
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
        public ProcessIdentity Identity => new(process.Id, process.StartTime.ToUniversalTime().Ticks);
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
