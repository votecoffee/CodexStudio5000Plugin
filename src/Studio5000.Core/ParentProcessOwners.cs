using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Studio5000.Core;

// Process handles pin identities; process names alone and saved PID inventories
// cannot establish ownership. This Windows profile never searches for children
// to kill and never treats owner loss as rollback of shared-service operations.
public sealed class ParentProcessOwners : IDisposable
{
    private readonly Process[] owners;
    private int disposed;
    private ParentProcessOwners(Process[] owners) => this.owners = owners;

    public static ParentProcessOwners Capture()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The owned Rockwell stdio process profile requires Windows.");
        var parents = SnapshotParents();
        var result = new List<Process>();
        using var current = Process.GetCurrentProcess();
        var childId = current.Id; var childStarted = current.StartTime.ToUniversalTime();
        try
        {
            for (var depth = 0; depth < 4; depth++)
            {
                if (!parents.TryGetValue(childId, out var parentId) || parentId <= 0 || parentId == childId)
                    throw new InvalidOperationException("Cannot establish the frontend's process owner.");
                var parent = Process.GetProcessById(parentId);
                result.Add(parent);
                // Open a handle while the identity is validated; a recycled PID
                // created after the child cannot become its owner.
                _ = parent.Handle;
                var started = parent.StartTime.ToUniversalTime();
                if (parent.HasExited || started > childStarted) throw new InvalidOperationException("The process owner exited or its identity changed.");
                if (parent.ProcessName.ToLowerInvariant() is not ("powershell" or "pwsh" or "cmd")) return new(result.ToArray());
                childId = parent.Id; childStarted = started;
            }
            throw new InvalidOperationException("The launcher ownership chain exceeds four processes.");
        }
        catch { foreach (var process in result) process.Dispose(); throw; }
    }

    public async Task RequestShutdownOnExitAsync(Action requestShutdown, CancellationToken ct)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var exits = owners.Select(p => p.WaitForExitAsync(waiting.Token)).ToArray();
        try
        {
            var completed = await Task.WhenAny(exits);
            if (!ct.IsCancellationRequested) { await completed; requestShutdown(); }
        }
        finally
        {
            waiting.Cancel();
            try { await Task.WhenAll(exits); } catch (OperationCanceledException) { }
        }
    }

    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) foreach (var owner in owners) owner.Dispose(); }

    private static Dictionary<int, int> SnapshotParents()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new InvalidOperationException("Cannot read process ownership metadata.");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
        if (!Process32First(snapshot, ref entry)) throw new InvalidOperationException("Cannot read process ownership metadata.");
        var result = new Dictionary<int, int>();
        do { result[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId); } while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new InvalidOperationException("Process ownership snapshot was incomplete.");
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeap;
        public uint ModuleId, ThreadCount, ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
