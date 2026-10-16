using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Mcp;

[McpServerToolType]
public sealed class WatchTools(ServiceClient client)
{
    [McpServerTool(Name = "studio_watch_save"), Description("Create a stopped watch list, or update a stopped list using its ID and expectedRevision. Tags identify exact base names/program scope; optional memberPath selects extracted nested fields/array cells. Supply a hash-bound ACD/L5X source, mode Online/Offline, intervalMs and historyLimit. Saving never connects; starting is separate.")]
    public Task<WatchStatus> Save(WatchListRequest request, CancellationToken ct, string? watchId = null, int? expectedRevision = null) =>
        watchId is null ? client.PostAsync<WatchStatus>("/api/watches", request, ct) :
            client.PostAsync<WatchStatus>("/api/watches/" + watchId, new WatchUpdateRequest(request, expectedRevision ?? -1), ct);

    [McpServerTool(Name = "studio_watch_list", ReadOnly = true), Description("List saved watch definitions, state and latest samples. This only reads the shared service cache; it does not start polling or connect.")]
    public Task<WatchStatus[]> List(CancellationToken ct) => client.GetAsync<WatchStatus[]>("/api/watches", ct);

    [McpServerTool(Name = "studio_watch_get", ReadOnly = true), Description("Read a watch list's cached values, per-tag quality/errors, timestamps, stale flag and extracted type definitions. No controller request is issued by this tool.")]
    public Task<WatchStatus> Get(string watchId, CancellationToken ct) => client.GetAsync<WatchStatus>("/api/watches/" + watchId, ct);

    [McpServerTool(Name = "studio_watch_start"), Description("Start interval polling in the shared service until explicitly stopped, faulted or the service exits. Online polling requires enabled controller access, a configured allowed target and user authorization to monitor these tags. Online operations only read; decoding uses a separate offline scratch project. No download, online tag writes or mode changes. Correlation/open failures fault the list without automatic reconnect. Lists never auto-start after service restart.")]
    public Task<WatchStatus> Start(string watchId, CancellationToken ct) => client.PostAsync<WatchStatus>("/api/watches/" + watchId + "/start", new { }, ct);

    [McpServerTool(Name = "studio_watch_stop"), Description("Stop a watch list's polling and dispose its reader session. Cached results remain available and stale. No controller tags are written.")]
    public Task<WatchStatus> Stop(string watchId, CancellationToken ct) => client.PostAsync<WatchStatus>("/api/watches/" + watchId + "/stop", new { }, ct);

    [McpServerTool(Name = "studio_watch_delete"), Description("Remove a stopped watch list from the shared catalog. Retained local source snapshots are preserved. Stop it before deleting.")]
    public Task<JsonElement> Delete(string watchId, CancellationToken ct) => client.PostAsync<JsonElement>("/api/watches/" + watchId + "/delete", new { }, ct);

    [McpServerTool(Name = "studio_watch_history", ReadOnly = true), Description("Read bounded in-memory polling history (oldest to newest). History resets on service restart; the latest sample persists as stale. This does not connect to a controller.")]
    public Task<WatchSnapshot[]> History(string watchId, CancellationToken ct, int limit = 100) => client.GetAsync<WatchSnapshot[]>("/api/watches/" + watchId + "/history?limit=" + limit, ct);

    [McpServerTool(Name = "studio_structure_extract", ReadOnly = true), Description("Extract named UDT definitions and decorated tag values as nested JSON structures/arrays from a managed Logix project or successful SDK upload job. Choose projectId or jobId, optionally dataTypeName/tagName/programName. Values are labeled saved/upload snapshots, not continuous live reads. Use a watch list for ongoing live values.")]
    public Task<StructureExtraction> Extract(StructureExtractRequest request, CancellationToken ct) => client.PostAsync<StructureExtraction>("/api/structures/extract", request, ct);

    [McpServerTool(Name = "studio_structure_pull"), Description("Upload the explicitly authorized configured controller into a new preserved SDK project to obtain its current UDT definitions and tag snapshot. Requires enabled controller access and UploadToNewProjectAsync in its target policy. No download, mode change or tag write. Returns an SDK job: wait for success, then pass jobId to studio_structure_extract. This is a potentially slow point-in-time upload, not interval polling.")]
    public Task<SdkJob> Pull(StructurePullRequest request, CancellationToken ct) => client.PostAsync<SdkJob>("/api/structures/pull", request, ct);
}
