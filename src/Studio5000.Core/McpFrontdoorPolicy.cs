using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio5000.Core;

// A reviewed, immutable frontend payload. It cannot change the service's
// worker/resource/job limits or grant an engineering operation.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpFrontdoorPolicy(int SchemaVersion = 1, int MaximumMetadataClients = 8,
    int MaximumStartingClients = 2, int MaximumForwardedCalls = 4,
    long PerClientCommitReservationBytes = 268435456, long MaximumTransportCommitReservationBytes = 2684354560,
    long MinimumAvailableCommitBytes = 2147483648, long MaximumProcessPrivateBytes = 134217728,
    int MaximumInputFrameBytes = 2097152, int MaximumHttpResponseBytes = 8388608,
    string ServiceMode = McpFrontdoorPolicy.EnsurePinnedOffline)
{
    public const string EnsurePinnedOffline = "ensure-pinned-offline";
    public const string ExistingGenuineOnly = "existing-genuine-only";
    public const string FileName = "frontdoor.json";
    public const int SlotInventory = 64;
    public void Validate()
    {
        if (ServiceMode is not (EnsurePinnedOffline or ExistingGenuineOnly) ||
            SchemaVersion != 1 || MaximumMetadataClients is < 1 or > 48 || MaximumStartingClients is < 1 or > 8 ||
            MaximumForwardedCalls is < 1 or > 16 || MaximumStartingClients > MaximumMetadataClients ||
            PerClientCommitReservationBytes is < 67108864 or > 1073741824 ||
            MaximumTransportCommitReservationBytes is < 67108864 or > 34359738368 ||
            checked((long)MaximumMetadataClients * PerClientCommitReservationBytes) > MaximumTransportCommitReservationBytes ||
            MinimumAvailableCommitBytes is < 536870912 or > 17179869184 ||
            MaximumProcessPrivateBytes is < 67108864 or > 536870912 ||
            MaximumProcessPrivateBytes + 134217728 > PerClientCommitReservationBytes ||
            MaximumInputFrameBytes is < 65536 or > 8388608 || MaximumHttpResponseBytes is < 1048576 or > 33554432)
            throw new ArgumentException("Invalid bounded MCP frontdoor admission policy.");
    }
    public static McpFrontdoorPolicy? Read(string backend)
    {
        var path = Path.Combine(Path.GetFullPath(backend), FileName);
        if (!File.Exists(path)) return null; // Older, explicitly selected profile.
        using var document = OfflineServicePin.Read(path, 8192);
        var value = document.Deserialize<McpFrontdoorPolicy>(OfflineServicePin.Json) ?? throw new ArgumentException("Missing MCP frontdoor policy.");
        value.Validate(); return value;
    }
    public void CheckReservation(int otherClients, ulong availableCommit)
    {
        Validate();
        if (otherClients < 0 || otherClients >= MaximumMetadataClients ||
            checked((long)(otherClients + 1) * PerClientCommitReservationBytes) > MaximumTransportCommitReservationBytes)
            throw new OperationException("mcp_frontdoor_commit_limit", "The reviewed transport reservation is full; no existing client was stopped.");
        if (availableCommit < (ulong)MinimumAvailableCommitBytes + (ulong)PerClientCommitReservationBytes)
            throw new OperationException("mcp_frontdoor_memory_pressure", "Available system commit is below the reviewed transport admission floor.");
    }
    public void CheckCurrentProcess()
    {
        using var process = Process.GetCurrentProcess();
        if (process.PrivateMemorySize64 > MaximumProcessPrivateBytes)
            throw new OperationException("mcp_frontdoor_memory_pressure", "This transport exceeded its reviewed private-commit admission allowance; no further request was forwarded.");
    }
    public static ulong AvailableCommit()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return status.AvailablePageFile;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Physical, AvailablePhysical, PageFile, AvailablePageFile, Virtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
