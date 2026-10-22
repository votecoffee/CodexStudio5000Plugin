using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Studio5000.Core.Resources;

public sealed class WindowsResourceProcessDriver : IResourceProcessDriver
{
    public string? HostId { get; }
    public WindowsResourceProcessDriver()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string value && Guid.TryParse(value, out var guid))
                HostId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guid.ToString("D").ToLowerInvariant()))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
    public static string DefaultRegistry()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native resource registry requires Windows.");
        var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        var error = SHGetKnownFolderPath(ref folder, 0x10000, IntPtr.Zero, out var result);
        if (error != 0) Marshal.ThrowExceptionForHR(error);
        try { return Path.Combine(Marshal.PtrToStringUni(result) ?? throw new InvalidOperationException("Local AppData is unavailable."), "CodexMCPManager", "Registry"); }
        finally { Marshal.FreeCoTaskMem(result); }
    }
    public ProcessIdentity CurrentIdentity() { using var current = Process.GetCurrentProcess(); return Capture((uint)current.Id); }
    public ProcessIdentity Capture(uint pid)
    {
        using var handle = OpenPid(pid, false);
        if (!handle.Alive) throw new ResourceProcessExitedException();
        return handle.Identity;
    }
    public string State(ProcessIdentity identity)
    {
        // Check the host before any local PID lookup, including a foreign claim
        // whose PID happens to match a process on this computer.
        if (!OperatingSystem.IsWindows() || HostId is null || identity.HostId != HostId) return "unknown";
        try
        {
            using var handle = OpenPid(identity.Pid, false);
            if (!handle.Alive) return "absent";
            ProcessIdentity observed;
            try { observed = handle.Identity; }
            catch (Win32Exception) { return handle.Alive ? "unknown" : "absent"; }
            if (observed.Created != identity.Created) return "different";
            if (observed.Executable != identity.Executable) return "unknown";
            return handle.Alive ? "alive" : "absent";
        }
        catch (ResourceProcessExitedException) { return "absent"; }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException) { return "unknown"; }
    }
    public IResourceProcessHandle Open(ProcessIdentity identity)
    {
        if (HostId is null || identity.HostId != HostId) throw new InvalidOperationException("Foreign or unverifiable resource host; no local PID may be used.");
        var handle = OpenPid(identity.Pid, true);
        try
        {
            if (!handle.Alive || handle.Identity != identity) throw new ResourceProcessExitedException();
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal ProcessIdentity CaptureHandle(SafeProcessHandle handle, uint pid)
    {
        if (HostId is null) throw new InvalidOperationException("Cannot verify the local resource host.");
        if (!GetProcessTimes(handle, out var created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var image = new StringBuilder(32768); var length = (uint)image.Capacity;
        if (!QueryFullProcessImageName(handle, 0, image, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ProcessIdentity(HostId, pid, created.ToString(CultureInfo.InvariantCulture), image.ToString()).Normalize();
    }
    internal ProcessIdentity? CaptureJobMember(uint pid, SafeFileHandle job)
    {
        using var handle = OpenPid(pid, false);
        if (!handle.Alive) return null;
        if (!IsProcessInJob(handle.SafeHandle, job, out var member)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return member ? handle.Identity : null;
    }
    private NativeHandle OpenPid(uint pid, bool terminate)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (pid < 5) throw new ArgumentException("System/invalid resource process ID.");
        var handle = OpenProcess(0x100000 | 0x1000u | (terminate ? 1u : 0u), false, pid);
        if (!handle.IsInvalid) return new(this, handle, pid);
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        if (error == 87) throw new ResourceProcessExitedException();
        throw new Win32Exception(error);
    }
    private sealed class NativeHandle(WindowsResourceProcessDriver driver, SafeProcessHandle handle, uint pid) : IResourceProcessHandle
    {
        internal SafeProcessHandle SafeHandle => handle;
        private ProcessIdentity? identity;
        public ProcessIdentity Identity => identity ??= driver.CaptureHandle(handle, pid);
        public bool Alive => WaitResult(0) == 258;
        private uint WaitResult(uint milliseconds)
        {
            var result = WaitForSingleObject(handle, milliseconds);
            if (result is not (0 or 258)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        public void Terminate() { if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public bool Wait(int milliseconds) => WaitResult(checked((uint)milliseconds)) == 0;
        public void Dispose() => handle.Dispose();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong exited, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool member);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}

public sealed class ResourceProcessExitedException : InvalidOperationException
{
    public ResourceProcessExitedException() : base("The exact resource process is no longer present.") { }
}
