using System.Net;
using System.Security.Cryptography;
using System.Text;
using Studio5000.Core;
using Studio5000.Extensibility;

var root = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = root });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.MaxDepth = 128);
builder.Configuration.AddJsonFile(Path.Combine(root, "service.local.json"), optional: true, reloadOnChange: false);
var url = builder.Configuration["ServiceUrl"] ?? "http://127.0.0.1:5487";
if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "http" ||
    !IPAddress.TryParse(address.Host, out var ip) || !IPAddress.IsLoopback(ip))
    throw new InvalidOperationException("ServiceUrl must be an HTTP loopback IP address.");
builder.WebHost.UseUrls(url);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost;[::1]";
var options = builder.Configuration.GetSection("Studio5000").Get<ServiceOptions>() ?? new();
options.StateDirectory = Path.GetFullPath(options.StateDirectory, root);
options.AllowedRoots = options.AllowedRoots.Select(p => Path.GetFullPath(p, root)).ToArray();
options.OutputRoots = options.OutputRoots.Select(p => Path.GetFullPath(p, root)).ToArray();
if (options.NativeBridgePath != null) options.NativeBridgePath = Path.GetFullPath(options.NativeBridgePath, root);
options.FactoryTalkSchemaDirectory = Path.GetFullPath(options.FactoryTalkSchemaDirectory, root);
options.FactoryTalkStudioPath = Path.GetFullPath(options.FactoryTalkStudioPath, root);
options.ViewDesignerPath = Path.GetFullPath(options.ViewDesignerPath, root);
options.ExtensionsDirectory = Path.GetFullPath(options.ExtensionsDirectory, root);
options.ExtensionWorkerPath = Path.GetFullPath(options.ExtensionWorkerPath, root);
var local = Path.Combine(root, ".local");
Directory.CreateDirectory(local);
ProjectService.RejectLinks(local);
var tokenPath = Path.Combine(local, "service.key");
if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token = File.ReadAllText(tokenPath).Trim();
if (token.Length < 32) throw new InvalidOperationException("service.key is too short.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton(_ => new ExtensionHost(options, root));
var app = builder.Build();
_ = app.Services.GetRequiredService<ProjectService>(); // hold one exclusive process lock before publishing connection info
File.WriteAllText(Path.Combine(local, "connection.json"), System.Text.Json.JsonSerializer.Serialize(new { url, tokenFile = tokenPath }));
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Path != "/health")
        {
            var supplied = context.Request.Headers.Authorization.ToString();
            var expected = "Bearer " + token;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { code = "unauthorized", message = "A local service bearer token is required." }); return; }
        }
        if (context.Request.Headers.ContainsKey("Origin"))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { code = "origin_denied", message = "Browser-origin requests are not enabled." }); return; }
        await next(context);
    }
    catch (OperationException ex)
    {
        context.Response.StatusCode = ex.Code == "not_found" ? 404 : ex.Code is "stale_revision" or "state_tampered" or "draft_tampered" ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
    }
    catch (Exception ex) when (ex is System.Xml.XmlException or System.Xml.Schema.XmlSchemaException or System.Xml.XPath.XPathException or ArgumentException or System.Text.Json.JsonException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_request", message = ex.Message }); }
});
app.MapGet("/health", () => new { status = "ok", name = WorkspaceIdentity.Name, version = WorkspaceIdentity.Version });
app.MapGet("/api/capabilities", (ProjectService service) => service.Capabilities());
app.MapGet("/api/projects", (ProjectService service) => service.Projects());
app.MapPost("/api/projects/compare", (ProjectService service, ProjectCompareRequest request) => service.CompareProjects(request));
app.MapPost("/api/projects/{id}/ladder-preview", (ProjectService service, string id, LadderPreviewRequest request) => service.PreviewLadder(id, request));
app.MapPost("/api/projects", (ProjectService service, RegisterRequest request, CancellationToken ct) => service.RegisterAsync(request.Path, ct));
app.MapPost("/api/projects/from-exploded", (ProjectService service, RegisterRequest request, CancellationToken ct) => service.RegisterExplodedAsync(request.Path, ct));
app.MapGet("/api/projects/{id}", (ProjectService service, string id) => service.Get(id));
app.MapGet("/api/projects/{id}/elements", (ProjectService service, string id, string? kind, string? query, int? offset, int? limit) => service.Inspect(id, kind, query, offset ?? 0, limit ?? 200));
app.MapGet("/api/projects/{id}/element", (ProjectService service, string id, string xpath) => service.ReadElement(id, xpath));
app.MapPost("/api/projects/{id}/previews", (ProjectService service, string id, PreviewRequest request, CancellationToken ct) => service.PreviewAsync(id, request, ct));
app.MapGet("/api/projects/{id}/previews/{draftId}", (ProjectService service, string id, string draftId) => service.Draft(id, draftId));
app.MapPost("/api/projects/{id}/previews/{draftId}/apply", (ProjectService service, string id, string draftId, ApplyRequest request, CancellationToken ct) => service.ApplyAsync(id, draftId, request, ct));
app.MapPost("/api/projects/{id}/exports", (ProjectService service, string id, ExportRequest request, CancellationToken ct) => service.ExportAsync(id, request, ct));
app.MapGet("/api/artifacts/{id}", (ProjectService service, string id) =>
{
    var (artifact, path) = service.Download(id);
    return Results.File(path, "application/octet-stream", $"{artifact.ProjectId}.{artifact.Format}");
});
app.MapPost("/api/artifacts/{id}/save", (ProjectService service, string id, SaveArtifactRequest request, CancellationToken ct) => service.SaveArtifactAsync(id, request.Path, ct));
app.MapGet("/api/audit", (ProjectService service, string? projectId) => service.History(projectId));
app.MapGet("/api/sdk/capabilities", (ProjectService service) => service.SdkCapabilities());
app.MapGet("/api/sdk/coverage", (ProjectService service, CancellationToken ct) => service.SdkCoverageAsync(ct));
app.MapPost("/api/sdk/source", (ProjectService service, RegisterRequest request, CancellationToken ct) => service.InspectSdkSourceAsync(request.Path, ct));
app.MapPost("/api/sdk/online-edits/plan", (ProjectService service, OnlineEditPlanRequest request, CancellationToken ct) => service.PrepareOnlineEditAsync(request, ct));
app.MapGet("/api/sdk/jobs", (ProjectService service) => service.SdkJobList());
app.MapPost("/api/sdk/jobs", (ProjectService service, SdkRunRequest request, CancellationToken ct) => service.StartSdkJobAsync(request, ct));
app.MapGet("/api/sdk/jobs/{id}", (ProjectService service, string id) => service.SdkJob(id));
app.MapPost("/api/sdk/jobs/{id}/cancel", (ProjectService service, string id) => service.CancelSdkJob(id));
app.MapGet("/api/watches", (ProjectService service) => service.WatchLists());
app.MapPost("/api/projects/{id}/cip-watch-plan", (ProjectService service, string id, CipWatchPlanRequest request) => service.PlanCipWatch(id, request));
app.MapPost("/api/watches/preflight", (ProjectService service, WatchPreflightRequest request, CancellationToken ct) => service.PreflightWatchAsync(request, ct));
app.MapGet("/api/watches/{id}/preflight", (ProjectService service, string id, CancellationToken ct) => service.PreflightSavedWatchAsync(id, ct));
app.MapPost("/api/watches/{id}/template", (ProjectService service, string id, WatchTemplateExportRequest request) => service.ExportWatchTemplate(id, request));
app.MapPost("/api/watches/{id}/rebind-preview", (ProjectService service, string id, WatchRebindPreviewRequest request, CancellationToken ct) => service.PreviewWatchRebindAsync(id, request, ct));
app.MapPost("/api/watches/{id}/rebind", (ProjectService service, string id, WatchRebindApplyRequest request, CancellationToken ct) => service.ApplyWatchRebindAsync(id, request, ct));
app.MapPost("/api/watch-templates/preview", (ProjectService service, WatchTemplatePreviewRequest request, CancellationToken ct) => service.PreviewWatchTemplateAsync(request, ct));
app.MapPost("/api/watch-templates/import", (ProjectService service, WatchTemplateImportRequest request, CancellationToken ct) => service.ImportWatchTemplateAsync(request, ct));
app.MapPost("/api/watches", (ProjectService service, WatchListRequest request, CancellationToken ct) => service.SaveWatchAsync(request, ct: ct));
app.MapGet("/api/watches/{id}", (ProjectService service, string id) => service.GetWatch(id));
app.MapPost("/api/watches/{id}", (ProjectService service, string id, WatchUpdateRequest request, CancellationToken ct) => service.SaveWatchAsync(request.Settings, id, request.ExpectedRevision, ct));
app.MapPost("/api/watches/{id}/start", (ProjectService service, string id) => service.StartWatchAsync(id));
app.MapPost("/api/watches/{id}/stop", (ProjectService service, string id) => service.StopWatchAsync(id));
app.MapPost("/api/watches/{id}/delete", (ProjectService service, string id) => { service.DeleteWatch(id); return new { deleted = true }; });
app.MapGet("/api/watches/{id}/history", (ProjectService service, string id, int? limit) => service.WatchHistory(id, limit ?? 100));
app.MapPost("/api/structures/extract", (ProjectService service, StructureExtractRequest request) => service.ExtractStructures(request));
app.MapPost("/api/structures/pull", (ProjectService service, StructurePullRequest request, CancellationToken ct) => service.PullStructuresAsync(request, ct));
app.MapPost("/api/tags/browse", (ProjectService service, TagBrowseRequest request) => service.BrowseTags(request));
app.MapPost("/api/tags/watch", (ProjectService service, BrowserWatchRequest request, CancellationToken ct) => service.CreateBrowserWatchAsync(request, ct));
app.MapGet("/api/watches/{id}/recording", (ProjectService service, string id) => service.RecordingStatus(id));
app.MapPost("/api/watches/{id}/recording", (ProjectService service, string id, WatchRecordingUpdate request) => service.ConfigureRecording(id, request));
app.MapPost("/api/watches/{id}/recording/query", (ProjectService service, string id, WatchRecordingQuery request) => service.QueryRecording(id, request));
app.MapPost("/api/watches/{id}/recording/export", (ProjectService service, string id, WatchRecordingQuery request) => service.ExportRecording(id, request));
app.MapGet("/api/factorytalk/status", (ProjectService service) => service.FactoryTalkStatus());
app.MapPost("/api/projects/{id}/graphics-references", (ProjectService service, string id, GraphicsReferenceRequest request) => service.GraphicsTagReferences(id, request));
app.MapPost("/api/projects/{id}/graphics-reference-resolution", (ProjectService service, string id, GraphicsResolveRequest request) => service.ResolveGraphicsReferences(id, request));
app.MapPost("/api/projects/{id}/tag-references", (ProjectService service, string id, TagReferenceRequest request) => service.FindTagReferences(id, request));
app.MapPost("/api/factorytalk/launch", (ProjectService service) => service.LaunchFactoryTalkStudio());
app.MapGet("/api/projects/{id}/graphics-validation", (ProjectService service, string id) => service.ValidateGraphics(id));
app.MapPost("/api/projects/{id}/graphics-imports", (ProjectService service, string id, PrepareGraphicsImportRequest request, CancellationToken ct) => service.PrepareGraphicsImportAsync(id, request, ct));
app.MapGet("/api/factorytalk/imports/{planId}", (ProjectService service, string planId) => service.GraphicsPlan(planId));
app.MapPost("/api/factorytalk/imports/{planId}/verify", (ProjectService service, string planId, VerifyGraphicsImportRequest request, CancellationToken ct) => service.VerifyGraphicsImportAsync(planId, request, ct));
app.MapGet("/api/viewdesigner/status", (ProjectService service) => service.ViewDesignerStatus());
app.MapGet("/api/viewdesigner/projects", (ProjectService service) => service.ViewDesignerProjects());
app.MapPost("/api/viewdesigner/projects", (ProjectService service, RegisterRequest request, CancellationToken ct) => service.RegisterViewDesignerAsync(request.Path, ct));
app.MapGet("/api/viewdesigner/projects/{id}", (ProjectService service, string id) => service.InspectViewDesigner(id));
app.MapGet("/api/viewdesigner/projects/{id}/file", (ProjectService service, string id, string file) => service.ReadViewDesignerFile(id, file));
app.MapPost("/api/viewdesigner/projects/{id}/previews", (ProjectService service, string id, ViewDesignerPreviewRequest request, CancellationToken ct) => service.PreviewViewDesignerAsync(id, request, ct));
app.MapPost("/api/viewdesigner/projects/{id}/previews/{previewId}/apply", (ProjectService service, string id, string previewId, ApplyRequest request, CancellationToken ct) => service.ApplyViewDesignerAsync(id, previewId, request, ct));
app.MapPost("/api/viewdesigner/projects/{id}/imports", (ProjectService service, string id, ViewDesignerImportRequest request, CancellationToken ct) => service.PrepareViewDesignerImportAsync(id, request, ct));
app.MapPost("/api/viewdesigner/imports/{planId}/verify", (ProjectService service, string planId, VerifyGraphicsImportRequest request, CancellationToken ct) => service.VerifyViewDesignerImportAsync(planId, request, ct));
app.MapGet("/api/extensions", async (ExtensionHost host, bool? includeSchemas, string? extensionId, CancellationToken ct) =>
{
    var catalog = await host.CatalogAsync(ct);
    if (extensionId is not null) catalog = catalog with { Extensions = catalog.Extensions.Where(x => x.Id == extensionId).ToArray() };
    if (includeSchemas == false) return Results.Json(new
    {
        catalog.ProtocolVersion, catalog.LoadErrors,
        extensions = catalog.Extensions.Select(x => new { x.Id, x.Version, x.Generation, x.PackageSha256, x.ActiveCalls, x.State,
            operations = x.Operations.Select(o => new { o.Name, o.Description, o.Effect }) })
    });
    return Results.Json(catalog);
});
app.MapGet("/api/extensions/operation", (ExtensionHost host, string extensionId, string operation, string generation, CancellationToken ct) => host.DescribeAsync(extensionId, operation, generation, ct));
app.MapPost("/api/extensions/reload", (ExtensionHost host, ExtensionReloadRequest request, CancellationToken ct) => host.ReloadAsync(request, ct));
app.MapPost("/api/extensions/invoke/{effect}", (ExtensionHost host, string effect, ExtensionCall request, CancellationToken ct) => host.InvokeAsync(effect, request, ct));
app.MapGet("/api/extensions/jobs", (ExtensionHost host) => host.Jobs());
app.MapGet("/api/extensions/jobs/{id}", (ExtensionHost host, string id) => host.Job(id));
app.MapPost("/api/extensions/jobs", (ExtensionHost host, ExtensionJobRequest request, CancellationToken ct) => host.StartJobAsync(request, ct));
app.MapPost("/api/extensions/jobs/{id}/cancel", (ExtensionHost host, string id) => host.CancelJob(id));
await app.RunAsync();

public partial class Program { }
