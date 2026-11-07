using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameBooster.Services;

public sealed record ProcessIdentity(int Id, long StartedUtcTicks);
public sealed record ProcessState(ProcessIdentity Identity, int Priority);
public sealed record PrioritySnapshot(ProcessIdentity Identity, int Previous, int Applied);

public interface IProcessAccess
{
    ProcessState? Read(int processId);
    void SetPriority(ProcessIdentity identity, int priority);
}

public sealed class WindowsProcessAccess : IProcessAccess
{
    public ProcessState? Read(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return null;
            return new(new(process.Id, process.StartTime.ToUniversalTime().Ticks), (int)process.PriorityClass);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public void SetPriority(ProcessIdentity identity, int priority)
    {
        using var process = Process.GetProcessById(identity.Id);
        // PID alone is not an identity: Windows can reuse it after a process exits.
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedUtcTicks)
            throw new InvalidOperationException("The game process has already ended.");
        process.PriorityClass = (ProcessPriorityClass)priority;
    }
}

public static class GamePriority
{
    public const int AboveNormal = (int)ProcessPriorityClass.AboveNormal;
    private static readonly int[] AllowedOriginalPriorities = [(int)ProcessPriorityClass.Idle, (int)ProcessPriorityClass.BelowNormal, (int)ProcessPriorityClass.Normal];

    public static void Apply(IProcessAccess access, PrioritySnapshot snapshot)
    {
        Validate(snapshot);
        var current = access.Read(snapshot.Identity.Id);
        if (current is null || current.Identity != snapshot.Identity) return;
        if (current.Priority != snapshot.Previous) throw new InvalidOperationException("The game changed its own CPU priority. Its preference was kept.");
        // Never lower a game that already chose a more urgent class.
        if (current.Priority is (int)ProcessPriorityClass.High or (int)ProcessPriorityClass.RealTime)
            throw new InvalidOperationException("The game already manages its own CPU priority.");
        access.SetPriority(snapshot.Identity, snapshot.Applied);
        current = access.Read(snapshot.Identity.Id);
        if (current is not null && current.Identity == snapshot.Identity && current.Priority != snapshot.Applied)
            throw new InvalidOperationException("The game CPU priority could not be verified.");
    }

    public static string Restore(IProcessAccess access, PrioritySnapshot snapshot)
    {
        Validate(snapshot);
        var current = access.Read(snapshot.Identity.Id);
        if (current is null || current.Identity != snapshot.Identity) return "The game process ended; its priority setting is no longer active.";
        if (current.Priority == snapshot.Previous) return "Original game CPU priority already restored.";
        if (current.Priority != snapshot.Applied) throw new InvalidOperationException("The game CPU priority changed outside GameBooster. Restore its original priority before retrying recovery.");
        access.SetPriority(snapshot.Identity, snapshot.Previous);
        current = access.Read(snapshot.Identity.Id);
        if (current is not null && current.Identity == snapshot.Identity && current.Priority != snapshot.Previous)
            throw new InvalidOperationException("The original game CPU priority could not be verified.");
        return "Original game CPU priority restored and verified.";
    }

    private static void Validate(PrioritySnapshot snapshot)
    {
        if (snapshot.Identity.Id <= 0 || snapshot.Identity.StartedUtcTicks <= 0 || !AllowedOriginalPriorities.Contains(snapshot.Previous) || snapshot.Applied != AboveNormal)
            throw new InvalidOperationException("The saved game priority snapshot needs manual review.");
    }
}

public interface ISessionAwakeFactory { IDisposable Create(); }

public sealed class WindowsSessionAwakeFactory : ISessionAwakeFactory
{
    public IDisposable Create() => new AwakeRequest();

    private sealed class AwakeRequest : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private bool _display;
        private bool _system;
        private bool _disposed;

        public AwakeRequest()
        {
            var reason = new ReasonContext { Version = 0, Flags = 1, Reason = "GameBooster gaming session" };
            _handle = PowerCreateRequest(ref reason);
            if (_handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error, "Cannot request idle-sleep prevention."); }
            try
            {
                if (!PowerSetRequest(_handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _system = true;
                if (!PowerSetRequest(_handle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                _display = true;
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            // Closing the handle also releases the requests if the app crashes.
            try
            {
                if (_display) PowerClearRequest(_handle, 0);
                if (_system) PowerClearRequest(_handle, 1);
            }
            finally { _handle.Dispose(); _disposed = true; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 32)]
    private struct ReasonContext
    {
        public uint Version, Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerSetRequest(SafeFileHandle handle, int type);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PowerClearRequest(SafeFileHandle handle, int type);
}
