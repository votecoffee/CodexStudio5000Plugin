using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Mcp;

[McpServerToolType]
public sealed class SdkTools(ServiceClient client)
{
    [McpServerTool(Name = "studio_sdk_catalog", ReadOnly = true), Description("List all 59 installed SDK 2.1.974 operations, exact parameter types, enums, target configuration and evidence boundary. Factories start a job; other calls share its project session.")]
    public Task<JsonElement> Catalog(CancellationToken ct) => client.GetAsync<JsonElement>("/api/sdk/capabilities", ct);

    [McpServerTool(Name = "studio_sdk_coverage", ReadOnly = true), Description("Compare the installed native SDK public signatures with the frozen 2.1.974 surface. This checks API coverage, not execution or controller acceptance.")]
    public Task<JsonElement> Coverage(CancellationToken ct) => client.GetAsync<JsonElement>("/api/sdk/coverage", ct);

    [McpServerTool(Name = "studio_sdk_inspect_source", ReadOnly = true), Description("Read the exact SHA-256 and size of an allowed ACD/L5X/L5K source or XML import file before a native SDK job.")]
    public Task<JsonElement> InspectSource(string path, CancellationToken ct) => client.PostAsync<JsonElement>("/api/sdk/source", new RegisterRequest(path), ct);

    [McpServerTool(Name = "studio_sdk_start_job", Destructive = true), Description("Run native SDK operations from studio_sdk_catalog in order on a preserved copy. Supply calls with exact method names and argument objects. Choose projectId, sourcePath or sourceArtifactId with expectedSha256, or start with CreateNewProjectAsync. File imports use inputFiles aliases bound to absolute path/SHA-256. Output paths are simple new filenames. Controller calls require an enabled configured controllerTargetId and explicit user authorization for that target/action. Password arguments are transient and redacted. Returns a job ID; poll before further action. Do not retry an indeterminate job automatically.")]
    public Task<JsonElement> StartJob(SdkCall[] calls, CancellationToken ct, string? projectId = null, string? expectedSha256 = null,
        string? sourcePath = null, string? sourceArtifactId = null, string? controllerTargetId = null, Dictionary<string, SdkInputFile>? inputFiles = null) =>
        client.PostAsync<JsonElement>("/api/sdk/jobs", new SdkRunRequest(calls, projectId, expectedSha256, sourcePath, sourceArtifactId, controllerTargetId, inputFiles), ct);

    [McpServerTool(Name = "studio_sdk_get_job", ReadOnly = true), Description("Read an SDK job's terminal or current status, per-call results, redacted diagnostics, output artifacts and new result project. succeeded means calls and file publication completed; runtime validation remains separate.")]
    public Task<JsonElement> GetJob(string jobId, CancellationToken ct) => client.GetAsync<JsonElement>("/api/sdk/jobs/" + jobId, ct);

    [McpServerTool(Name = "studio_sdk_list_jobs", ReadOnly = true), Description("List the latest 100 SDK job results across Codex, REST and the desktop GUI.")]
    public Task<JsonElement> ListJobs(CancellationToken ct) => client.GetAsync<JsonElement>("/api/sdk/jobs", ct);

    [McpServerTool(Name = "studio_sdk_cancel_job", Destructive = true), Description("Request cancellation of a queued/running SDK process. Poll the job for completion. This does not roll back completed controller actions; interrupted execution can be indeterminate.")]
    public Task<JsonElement> CancelJob(string jobId, CancellationToken ct) => client.PostAsync<JsonElement>("/api/sdk/jobs/" + jobId + "/cancel", new { }, ct);
}
