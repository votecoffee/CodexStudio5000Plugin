using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Studio5000.Core.Resources;

// A private job and exact stdio handles are assigned DURING suspended creation.
// There is no Process.Start/late-assign window, shell fallback or global sweep.
public sealed class WindowsChildProcess : IResourceProcessGroup, IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WindowsChildProcess> Retained = new();
    private readonly ResourceLedger.ResourceOwner owner;
    private readonly WindowsResourceProcessDriver driver;
    private SafeFileHandle? job;
    private SafeProcessHandle? process;
    private SafeFileHandle? thread;
    private CleanupReceipt? receipt;
    private bool resumed;
    public string ResourceId { get; }
    public ProcessIdentity Identity { get; private set; } = null!;
    public int Id => checked((int)Identity.Pid);
    public StreamWriter StandardInput { get; private set; } = null!;
    public StreamReader StandardOutput { get; private set; } = null!;
    public StreamReader StandardError { get; private set; } = null!;
    public bool HasExited => process is null || WaitForSingleObject(process, 0) switch { 0 => true, 258 => false, _ => throw Error() };
    public int ExitCode { get { if (process is null || !GetExitCodeProcess(process, out var code)) throw Error(); return unchecked((int)code); } }

    private WindowsChildProcess(ResourceLedger.ResourceOwner owner, string id, WindowsResourceProcessDriver driver)
    { this.owner = owner; ResourceId = id; this.driver = driver; }
    public static WindowsChildProcess Launch(ResourceLedger.ResourceOwner owner, ProcessStartInfo start, string label,
        string kind = "process", int maximumProcesses = 32, Action<ResourceRecord>? beforeResume = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) throw new PlatformNotSupportedException("Atomic process-job creation requires Windows 10/Server 2016 or later.");
        if (owner.Ledger.Driver is not WindowsResourceProcessDriver driver) throw new ArgumentException("The native launcher requires the native process driver.");
        if (start.UseShellExecute || start.Arguments.Length != 0 || !Path.IsPathFullyQualified(start.FileName) || !File.Exists(start.FileName))
            throw new ArgumentException("Use an existing absolute executable and ArgumentList without shell execution.");
        ProjectService.RejectLinks(start.FileName);
        if (!string.IsNullOrEmpty(start.WorkingDirectory)) ProjectService.RejectLinks(Path.GetFullPath(start.WorkingDirectory));
        if (maximumProcesses is < 1 or > 128) throw new ArgumentException("Use 1..128 processes in the owned job.");
        var command = CommandLine([start.FileName, .. start.ArgumentList]);
        var environment = EnvironmentBlock(start.Environment);
        var intent = owner.BeginLaunch(label, kind, cleanupMode: "terminate");
        var child = new WindowsChildProcess(owner, intent.ResourceId, driver);
        try
        {
            child.Create(start.FileName, command, environment, string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory, maximumProcesses);
            var record = owner.BindProcess(intent.ResourceId, child.Identity.Pid, "windows_job");
            if (!Retained.TryAdd(child.ResourceId, child)) throw new InvalidOperationException("Owned process identity already exists.");
            beforeResume?.Invoke(record); // Fixture failure injection / app launch policy, never caller code from MCP.
            if (ResumeThread(child.thread!) == uint.MaxValue) throw Error();
            child.resumed = true; child.thread!.Dispose(); child.thread = null;
            return child;
        }
        catch
        {
            // No executable user code runs before binding is durable. If a
            // future change throws after resume, retain uncertainty instead.
            try
            {
                child.StopCore();
                if (!child.resumed) owner.FailBeforeExecution(intent.ResourceId);
                else owner.SetRecoveryBarrier(intent.ResourceId, "Worker executed before startup outcome was known; inspect retained work.");
            }
            finally { Retained.TryRemove(child.ResourceId, out _); child.CloseHandles(); }
            throw;
        }
    }
    public ResourceRecord Record => owner.Ledger.Get(ResourceId);
    public bool HandlesClosed => process is null;
    public ResourceRecord Refresh() => owner.PublishMembers(ResourceId, Identities());
    public void SetRecoveryBarrier(string reason) => owner.SetRecoveryBarrier(ResourceId, reason);
    public void ResolveRecoveryBarrier(string revision, string evidence) => owner.ResolveRecoveryBarrier(ResourceId, revision, evidence);
    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        var handle = process ?? throw new ObjectDisposedException(nameof(WindowsChildProcess));
        var retained = false; handle.DangerousAddRef(ref retained);
        try
        {
            using var wait = new EventWaitHandle(false, EventResetMode.AutoReset);
            var original = wait.SafeWaitHandle;
            wait.SafeWaitHandle = new SafeWaitHandle(handle.DangerousGetHandle(), ownsHandle: false); original.Dispose();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) => done.TrySetResult(), null, Timeout.Infinite, true);
            using var cancelled = ct.Register(() => done.TrySetCanceled(ct));
            try { await done.Task; } finally { registration.Unregister(null); }
        }
        finally { if (retained) handle.DangerousRelease(); }
    }
    public ProcessIdentity[] Identities()
    {
        if (job is null) return [];
        var size = 8 + IntPtr.Size * 256; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(job, 3, buffer, (uint)size, IntPtr.Zero)) throw Error();
            var count = Marshal.ReadInt32(buffer, 4); if (count is < 0 or > 256) throw new InvalidOperationException("Owned job membership exceeds its budget.");
            var result = new List<ProcessIdentity>();
            for (var i = 0; i < count; i++)
            {
                var pid = checked((uint)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size).ToInt64());
                try { var member = driver.CaptureJobMember(pid, job); if (member is not null) result.Add(member); }
                catch (ResourceProcessExitedException) { }
            }
            return result.OrderBy(p => p.Pid).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    void IResourceProcessGroup.Stop() => StopCore();
    private void StopCore()
    {
        if (job is null || job.IsInvalid) return;
        if (!TerminateJobObject(job, 1)) throw Error();
        var buffer = Marshal.AllocHGlobal(48);
        try
        {
            var deadline = Stopwatch.StartNew();
            do
            {
                if (!QueryInformationJobObject(job, 1, buffer, 48, IntPtr.Zero)) throw Error();
                if (Marshal.ReadInt32(buffer, 40) == 0) return;
                if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Owned process tree did not exit; preserve the cleanup receipt.");
                Thread.Sleep(10);
            } while (true);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public CleanupReceipt Close()
    {
        var record = receipt is null ? Record : null;
        // A refused native/dirty barrier deliberately keeps these handles rooted
        // in the owning supervisor. Dispose never turns refusal into silent kill.
        receipt ??= owner.Cleanup(ResourceId, record!.Revision, Guid.NewGuid().ToString("N"), this);
        try { CloseHandles(); } finally { Retained.TryRemove(ResourceId, out _); }
        return receipt;
    }
    public void Kill(bool entireProcessTree = true)
    {
        if (!entireProcessTree) throw new ArgumentException("Owned resources require exact whole-job cleanup.");
        receipt ??= owner.Cleanup(ResourceId, Record.Revision, Guid.NewGuid().ToString("N"), this);
        if (receipt.Outcome == "uncertain") throw new IOException("Owned cleanup is uncertain; inspect its retained receipt and do not retry.");
    }
    public void Dispose() => Close();
    private void CloseHandles()
    {
        foreach (var stream in new IDisposable?[] { StandardInput, StandardOutput, StandardError })
            try { stream?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        thread?.Dispose(); thread = null; process?.Dispose(); process = null; job?.Dispose(); job = null;
    }

    private void Create(string executable, string command, string environment, string cwd, int maximum)
    {
        job = CreateJobObject(IntPtr.Zero, null); if (job.IsInvalid) throw Error();
        var limits = new ExtendedLimits { Basic = new() { Flags = 0x2008, MaximumProcesses = (uint)maximum } };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw Error();
        var attributes = IntPtr.Zero; var jobList = IntPtr.Zero; var handles = IntPtr.Zero; var env = IntPtr.Zero; var initialized = false;
        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, errorRead = null, errorWrite = null;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, ref security, 0) || !CreatePipe(out outputRead, out outputWrite, ref security, 0) || !CreatePipe(out errorRead, out errorWrite, ref security, 0)) throw Error();
            foreach (var parent in new[] { inputWrite, outputRead, errorRead }) if (!SetHandleInformation(parent, 1, 0)) throw Error();
            nuint bytes = 0; _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
            if (bytes == 0) throw Error(); attributes = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes)) throw Error(); initialized = true;
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            var childHandles = new[] { inputRead, outputWrite, errorWrite };
            for (var i = 0; i < childHandles.Length; i++) Marshal.WriteIntPtr(handles, i * IntPtr.Size, childHandles[i].DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x2000d, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero) ||
                !UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw Error();
            var startup = new StartupInfoEx { Attributes = attributes, Startup = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x101,
                Input = inputRead.DangerousGetHandle(), Output = outputWrite.DangerousGetHandle(), Error = errorWrite.DangerousGetHandle() } };
            env = Marshal.StringToHGlobalUni(environment);
            if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x8080404, env, cwd, ref startup, out var info)) throw Error();
            process = new SafeProcessHandle(info.Process, true); thread = new SafeFileHandle(info.Thread, true);
            Identity = driver.CaptureHandle(process, info.Pid);
            inputRead.Dispose(); inputRead = null; outputWrite.Dispose(); outputWrite = null; errorWrite.Dispose(); errorWrite = null;
            StandardInput = new StreamWriter(new FileStream(inputWrite, FileAccess.Write, 4096, false), new UTF8Encoding(false)); inputWrite = null;
            StandardOutput = new StreamReader(new FileStream(outputRead, FileAccess.Read, 4096, false), new UTF8Encoding(false)); outputRead = null;
            StandardError = new StreamReader(new FileStream(errorRead, FileAccess.Read, 4096, false), new UTF8Encoding(false)); errorRead = null;
        }
        finally
        {
            inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose(); errorRead?.Dispose(); errorWrite?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(attributes);
            foreach (var memory in new[] { attributes, jobList, handles, env }) if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }
    public static string CommandLine(IEnumerable<string> args)
    {
        var items = args.ToArray(); if (items.Length is < 1 or > 256 || items.Any(a => a.Contains('\0'))) throw new ArgumentException("Invalid executable argument list.");
        string Quote(string value)
        {
            if (value.Length != 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
            var output = new StringBuilder("\""); var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); output.Append(c); slashes = 0;
            }
            return output.Append('\\', slashes * 2).Append('"').ToString();
        }
        var command = string.Join(' ', items.Select(Quote));
        if (command.Length > 32766) throw new ArgumentException("Windows command line exceeds its bound."); return command;
    }
    private static string EnvironmentBlock(IDictionary<string, string?> values)
    {
        if (values.Any(p => p.Key.Length == 0 || p.Key.Contains('=') || p.Key.Contains('\0') || p.Value?.Contains('\0') == true)) throw new ArgumentException("Invalid child environment.");
        var text = string.Join('\0', values.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0";
        if (text.Length > 1024 * 1024) throw new ArgumentException("Child environment exceeds its bound."); return text;
    }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        public ushort ShowWindow, ReservedBytes; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorking, MaxWorking; public uint MaximumProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr data, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
