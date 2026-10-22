using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Studio5000.Core.Resources;

public interface IUserShellHandoff : IDisposable
{
    ProcessIdentity ProviderIdentity { get; }
    void Launch(string executable);
}
public sealed record NativeGuiHandoffResult(string RequestId, string ResourceId, string Status,
    string ApplicationOwnership, string TargetInstanceIdentity, string Message);

// Launch an empty native GUI in the user's existing desktop shell. There is no
// native document connection to save/close and no private GUI added to the
// service's kill-on-close job. Subsequent document work belongs to the user.
public sealed class NativeGuiHandoff(NativeSessionRegistry sessions, string directory, Func<IUserShellHandoff> providerFactory)
{
    private readonly object sync = new();
    public NativeGuiHandoffResult Launch(string executable)
    {
        lock (sync)
        {
            executable = Path.GetFullPath(executable); ProjectService.RejectLinks(executable);
            if (!File.Exists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new OperationException("native_unavailable", "Configure an installed native GUI executable.");
            ProjectService.RejectLinks(directory); Directory.CreateDirectory(directory);
            var target = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(executable.ToLowerInvariant()))).ToLowerInvariant();
            var records = Directory.EnumerateFiles(directory, target + "-*.json").Take(4097).ToArray();
            if (records.Length > 4096) throw new IOException("GUI handoff history is full; preserve evidence before archival.");
            foreach (var path in records)
            {
                ProjectService.RejectLinks(path);
                if (new FileInfo(path).Length > 8192) throw new IOException("Invalid retained GUI handoff record.");
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                if (document.RootElement.GetProperty("state").GetString() != "handed_off") throw new OperationException("native_handoff_uncertain", "Inspect the retained shell handoff before requesting another launch; no automatic retry is allowed.");
            }
            var id = Guid.NewGuid().ToString("N"); var recordPath = Path.Combine(directory, target + "-" + id + ".json");
            using var provider = providerFactory(); // Captures/pins existing shell only; no COM dispatch yet.
            var lease = sessions.Attach(new("windows-desktop-shell", id, "com-instance", provider.ProviderIdentity), "attached");
            void Save(string state) => NativeLifecycleJournal.WriteAtomic(recordPath, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, requestId = id, resourceId = lease.ResourceId, targetSha256 = target, provider = provider.ProviderIdentity, state }));
            Save("dispatching");
            try
            {
                provider.Launch(executable);
                var close = lease.Close(() =>
                {
                    provider.Dispose();
                    return NativeCloseResult.Closed("Desktop-shell handoff completed and COM proxies detached; no native project/document was opened or owned by this app.");
                });
                if (close.Outcome != "closed") throw new IOException("Desktop shell detach result is uncertain.");
                Save("handed_off");
                return new(id, lease.ResourceId, "handed_off", "user", "not_returned_by_shell",
                    "Launch handed to the existing user desktop. Observe Studio to confirm startup; save/close its documents there. The service will not terminate or adopt that application.");
            }
            catch
            {
                if (lease.State.State == "open") lease.Close(() => NativeCloseResult.Uncertain("Desktop shell launch outcome is unknown; inspect the native desktop and retained handoff before retrying."));
                Save("uncertain"); throw;
            }
        }
    }
}

// Obtain Explorer's existing desktop automation object rather than creating a
// private GUI or using local ShellExecute/Process.Start. See Microsoft's
// IShellWindows::FindWindowSW and IShellDispatch2::ShellExecute documentation.
public sealed class WindowsUserShellHandoff : IUserShellHandoff
{
    private readonly WindowsResourceProcessDriver driver = new();
    private readonly IResourceProcessHandle provider;
    private readonly IntPtr desktop;
    private bool disposed;
    public ProcessIdentity ProviderIdentity { get; }
    public WindowsUserShellHandoff()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        desktop = GetShellWindow();
        if (desktop == IntPtr.Zero || GetWindowThreadProcessId(desktop, out var pid) == 0) throw new InvalidOperationException("An existing interactive desktop shell is required; no private launch fallback is permitted.");
        ProviderIdentity = driver.Capture(pid); provider = driver.Open(ProviderIdentity);
        try { VerifyDesktop(); } catch { provider.Dispose(); throw; }
    }
    private void VerifyDesktop()
    {
        if (!provider.Alive || GetShellWindow() != desktop || GetWindowThreadProcessId(desktop, out var pid) == 0 || pid != ProviderIdentity.Pid)
            throw new InvalidOperationException("Pinned desktop shell identity changed.");
    }
    public void Launch(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ObjectDisposedException.ThrowIf(disposed, this); VerifyDesktop();
        // A dedicated STA bounds COM ownership to this synchronous handoff.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var proxies = new List<object>();
            // Each COM return adds one RCW reference, even when two returned
            // interfaces share a managed wrapper. Balance each return once.
            T Keep<T>(T proxy) where T : class { proxies.Add(proxy); return proxy; }
            try
            {
                var windowsType = Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
                dynamic windows = Keep(Activator.CreateInstance(windowsType)!);
                object location = 0, root = 0; int hwnd = 0;
                object browser = Keep((object)windows.FindWindowSW(ref location, ref root, 8, out hwnd, 1));
                if (unchecked((uint)hwnd) != unchecked((uint)desktop.ToInt64())) throw new InvalidOperationException("COM desktop differs from the pinned shell window.");
                var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
                var browserId = typeof(IShellBrowser).GUID;
                ((IShellServiceProvider)browser).QueryService(ref service, ref browserId, out var browserObject); Keep(browserObject);
                ((IShellBrowser)browserObject).QueryActiveShellView(out var view); Keep(view);
                var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
                view.GetItemObject(0, ref dispatch, out var folderObject); dynamic folder = Keep(folderObject);
                dynamic shell = Keep((object)folder.Application);
                VerifyDesktop();
                shell.ShellExecute(executable, "", Path.GetDirectoryName(executable)!, "open", 1);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var proxy in proxies.AsEnumerable().Reverse())
                    try { if (Marshal.IsComObject(proxy)) Marshal.ReleaseComObject(proxy); }
                    catch (Exception ex) { failure ??= ex; }
            }
        }) { IsBackground = true, Name = "Rockwell desktop-shell handoff" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Desktop handoff timed out; retain its unknown result without retrying.");
        if (failure is not null) throw new IOException("Desktop handoff failed; inspect retained native state before retrying.", failure);
    }
    public void Dispose() { if (!disposed) { disposed = true; provider.Dispose(); } }
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider { void QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value); }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd); void ContextSensitiveHelp(int enter); void InsertMenusSB(IntPtr menu, IntPtr widths);
        void SetMenuSB(IntPtr menu, IntPtr hole, IntPtr window); void RemoveMenusSB(IntPtr menu); void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(int enable); void TranslateAcceleratorSB(IntPtr message, ushort id); void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream); void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint message, IntPtr wparam, IntPtr lparam, out IntPtr result);
        void QueryActiveShellView(out IShellView view);
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr hwnd); void ContextSensitiveHelp(int enter); void TranslateAccelerator(IntPtr message);
        void EnableModeless(int enable); void UIActivate(uint state); void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rectangle, out IntPtr hwnd);
        void DestroyViewWindow(); void GetCurrentInfo(IntPtr settings); void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr parameter);
        void SaveViewState(); void SelectItem(IntPtr pidl, uint flags);
        void GetItemObject(uint item, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
    }
}
