using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Studio5000.Core;

public sealed record ProcessContainmentStatus(string Profile, bool Enabled, int? MaximumProcesses,
    uint? ActiveProcesses, string ExternalNativeEffects, string DurableResourceRecovery);

// Enter before any app worker launches. Children inherit membership at creation,
// avoiding the race inherent in finding/assigning a child after Process.Start.
public static class OwnedProcessContainment
{
    public const int DefaultMaximumProcesses = 32;
    private const uint ActiveProcessLimit = 0x00000008, KillOnJobClose = 0x00002000;
    private static readonly object Gate = new();
    private static JobHandle? ownerJob;
    private static int maximum;

    public static void ValidateLimit(int value)
    {
        if (value is < 1 or > 128) throw new ArgumentException("MaximumOwnedProcesses must be between 1 and 128, including the owning service.");
    }

    public static void EnterCurrentProcess(int maximumProcesses)
    {
        ValidateLimit(maximumProcesses);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Owned service process containment requires Windows.");
        lock (Gate)
        {
            if (ownerJob is not null) throw new InvalidOperationException("This service already owns a process containment job; limits require a new service instance.");
            using var current = Process.GetCurrentProcess();
            var candidate = CreateJobObject(IntPtr.Zero, null); // unnamed, non-inheritable handle
            var assigned = false;
            try
            {
                if (candidate.IsInvalid) throw Error("create");
                var limits = new ExtendedLimits
                {
                    Basic = new() { Flags = ActiveProcessLimit | KillOnJobClose, MaximumProcesses = (uint)maximumProcesses }
                };
                if (!SetInformationJobObject(candidate, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Error("configure");
                // Root the only job handle before assignment. After assignment,
                // disposing it would kill this owner before journals/DI cleanup.
                // It deliberately stays rooted until OS process exit instead.
                ownerJob = candidate;
                if (!AssignProcessToJobObject(candidate, current.SafeHandle)) throw Error("assign");
                assigned = true;
                maximum = maximumProcesses;
            }
            finally
            {
                if (!assigned) { ownerJob = null; candidate.Dispose(); }
            }
        }
    }

    public static ProcessContainmentStatus Snapshot()
    {
        lock (Gate)
        {
            if (ownerJob is null) return new("not-entered", false, null, null, "unknown", "not-integrated");
            var known = QueryInformationJobObject(ownerJob, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero);
            return new(known ? "windows-job-runtime-unassessed" : "windows-job-count-unavailable", true, maximum,
                known ? accounting.ActiveProcesses : null, "unknown-outside-inherited-tree", "not-integrated");
        }
    }

    private static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), "Cannot " + stage + " the owned process job; service startup is refused before engineering work.");

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint MaximumProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, LimitTerminatedProcesses;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(JobHandle job, int informationClass, out Accounting information, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
