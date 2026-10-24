using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Studio5000.Core.Resources;

namespace Studio5000.Core;

// The pinned PowerShell wrapper gets only file-backed stdio handles. A shared
// service may inherit those files without retaining a frontend protocol pipe.
// This helper never registers itself as the service or owns its private job.
internal static class WindowsOfflineStarterProcess
{
    internal static async Task<int> RunAsync(ProcessStartInfo start, string output, Action<ProcessIdentity> started, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new SecurityAttributes { Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        ProjectService.RejectLinks(output + ".stdout.log"); ProjectService.RejectLinks(output + ".stderr.log");
        using var stdin = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
        using var stdout = CreateFile(output + ".stdout.log", 0x40000000, 1, ref security, 1, 0x80, IntPtr.Zero);
        using var stderr = CreateFile(output + ".stderr.log", 0x40000000, 1, ref security, 1, 0x80, IntPtr.Zero);
        if (stdin.IsInvalid || stdout.IsInvalid || stderr.IsInvalid) throw Error();
        nuint length = 0; _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref length);
        if (length == 0) throw Error();
        var attributes = Marshal.AllocHGlobal(checked((int)length)); var handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
        var environment = Marshal.StringToHGlobalUni(WindowsChildProcess.EnvironmentBlock(start.Environment)); var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref length)) throw Error(); initialized = true;
            var files = new[] { stdin, stdout, stderr };
            for (var n = 0; n < files.Length; n++) Marshal.WriteIntPtr(handles, n * IntPtr.Size, files[n].DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw Error();
            var startup = new StartupInfoEx { Attributes = attributes, Startup = new() { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x101,
                Input = stdin.DangerousGetHandle(), Output = stdout.DangerousGetHandle(), Error = stderr.DangerousGetHandle() } };
            var command = new StringBuilder(WindowsChildProcess.CommandLine([start.FileName, .. start.ArgumentList]));
            if (!CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, true, 0x8080400, environment, start.WorkingDirectory, ref startup, out var info)) throw Error();
            using var process = new SafeProcessHandle(info.Process, true); using var thread = new SafeFileHandle(info.Thread, true);
            started(new WindowsResourceProcessDriver().CaptureHandle(process, info.Pid));
            while (true)
            {
                ct.ThrowIfCancellationRequested(); var wait = WaitForSingleObject(process, 0);
                if (wait == 0) break;
                if (wait != 258) throw Error();
                await Task.Delay(50, ct);
            }
            if (!GetExitCodeProcess(process, out var code)) throw Error();
            return unchecked((int)code);
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(environment);
        }
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
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
