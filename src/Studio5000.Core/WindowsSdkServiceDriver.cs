using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

// Local fixed-target SCM handles only. No stop/restart-running/configuration,
// arbitrary service name, process kill, shell, elevation or controller API.
public sealed class WindowsSdkServiceDriver : ISdkServiceDriver
{
    public SdkServiceSnapshot Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SDK Windows service recovery requires Windows.");
        var host = new WindowsResourceProcessDriver().CurrentIdentity().HostId;
        using var manager = OpenSCManager(null, null, 1); Check(manager);
        using var service = OpenService(manager, SdkServiceControl.ServiceName, 4);
        if (service.IsInvalid && Marshal.GetLastWin32Error() == 1060) return new(SdkServiceControl.ServiceName, SdkServiceControl.DisplayName, "NotInstalled", host, Installed: false);
        Check(service);
        var display = new StringBuilder(513); uint size = 512;
        if (!GetServiceDisplayName(manager, SdkServiceControl.ServiceName, display, ref size)) throw Error(false);
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _)) throw Error(false);
        var state = status.State switch { 1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running", 5 => "ContinuePending", 6 => "PausePending", 7 => "Paused", _ => "Unknown" };
        return new(SdkServiceControl.ServiceName, display.ToString(), state, host, status.State is 4 or 5 or 6 or 7 ? status.Pid : 0, status.ExitCode);
    }
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var manager = OpenSCManager(null, null, 1); Check(manager, true);
        using var service = OpenService(manager, SdkServiceControl.ServiceName, 0x10); Check(service, true);
        if (!StartService(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1056) return; // Another client already started it; readback still required.
            throw new SdkServiceApiException(error, error is 5 or 1058 or 1060);
        }
    }
    private static void Check(ServiceHandle handle, bool beforeStart = false) { if (handle.IsInvalid) throw Error(beforeStart); }
    private static SdkServiceApiException Error(bool beforeStart) => new(Marshal.GetLastWin32Error(), beforeStart);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Controls, ExitCode, ServiceExitCode, CheckPoint, WaitHint, Pid, Flags; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "GetServiceDisplayNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetServiceDisplayName(ServiceHandle manager, string name, StringBuilder display, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
