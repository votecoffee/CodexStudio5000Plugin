using System.Net;
using System.Security.Cryptography;
using System.Text;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Core.Resources;

if (args is ["--version"]) { Console.WriteLine(WorkspaceIdentity.Version); return; }
if (args is ["--native-local-settings", var candidateRoot])
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(NativeExecutionPolicy.CreateLocalOptions(candidateRoot), ServiceSettingsStore.Json));
    return; // Pure configuration generation: no service/native/resource initialization.
}
var root = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
if (File.Exists(Path.Combine(root, ".local", "maintenance-hold.json"))) throw new InvalidOperationException("RockwellMCPServer is on operator maintenance hold; explicit human release is required.");
using var supervisorOwner = Environment.GetEnvironmentVariable("ROCKWELL_MANAGED_SERVICE") == "1" ? ParentProcessOwners.Capture() : null;
if (args.Length == 1 && args[0] == "--stage-broad-local-settings")
{
    var settings = ServiceSettingsStore.OpenOffline(root); var current = settings.Read();
    var result = settings.Save(new(current.Revision, settings.BroadLocalPreview(current.Saved)));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { saved = true, restartRequired = true,
        configurationPath = result.Settings.ConfigurationPath, backupPath = result.BackupPath,
        nativeConnectionAttempted = false, runningServiceChanged = false }));
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = root });
if (supervisorOwner is not null)
    builder.Services.AddHostedService(provider => new SupervisorExitService(supervisorOwner, provider.GetRequiredService<IHostApplicationLifetime>()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.MaxDepth = 128);
builder.Configuration.AddJsonFile(Path.Combine(root, "service.local.json"), optional: true, reloadOnChange: false);
var url = builder.Configuration["ServiceUrl"] ?? "http://127.0.0.1:5487";
if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "http" ||
    !IPAddress.TryParse(address.Host, out var ip) || !IPAddress.IsLoopback(ip))
    throw new InvalidOperationException("ServiceUrl must be an HTTP loopback IP address.");
builder.WebHost.UseUrls(url);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost;[::1]";
var settingsSection = builder.Configuration.GetSection("Studio5000");
var options = new ServiceOptions();
// Bind configured arrays onto empty arrays rather than appending their defaults.
var configuredKeys = settingsSection.GetChildren().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
if (configuredKeys.Contains(nameof(ServiceOptions.AllowedRoots))) options.AllowedRoots = [];
if (configuredKeys.Contains(nameof(ServiceOptions.OutputRoots))) options.OutputRoots = [];
settingsSection.Bind(options);
ServiceSettingsStore.ResolveAndValidate(options, root);
if (supervisorOwner is not null) ManagedResourcePolicy.Validate(Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_PROFILE"), options);
// Self-membership precedes all business service/worker initialization. Never
// assign an existing user-owned engineering application to this private job.
OwnedProcessContainment.EnterCurrentProcess(options.MaximumOwnedProcesses);
using var resourceSupervisor = new ResourceSupervisor(root,
    Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_REGISTRY") ?? WindowsResourceProcessDriver.DefaultRegistry(), options.MaximumOwnedProcesses, options.MaximumOwnedResources);
if (supervisorOwner is not null) ManagedServiceAdmission.WaitForOwner(root, resourceSupervisor.Ledger);
var local = Path.Combine(root, ".local");
Directory.CreateDirectory(local);
ProjectService.RejectLinks(local);
var tokenPath = Path.Combine(local, "service.key");
if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token = File.ReadAllText(tokenPath).Trim();
if (token.Length < 32) throw new InvalidOperationException("service.key is too short.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IHostLifetime>(new OwnerConsoleLifetime(new OwnerShutdownMailbox(root, resourceSupervisor.Owner.OwnerId, token)));
builder.Services.AddSingleton(new ServiceSettingsStore(root, url, options));
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton(_ => new ExtensionHost(options, root));
builder.Services.AddHostedService(provider => new OwnerStopService(root, resourceSupervisor, token,
    provider.GetRequiredService<ProjectService>(), provider.GetRequiredService<ExtensionHost>(), provider.GetRequiredService<IHostApplicationLifetime>()));
builder.Services.AddHostedService(_ => new ResourceRefreshService(resourceSupervisor));
var app = builder.Build();
_ = app.Services.GetRequiredService<ProjectService>(); // hold one exclusive process lock before publishing connection info
resourceSupervisor.WriteOwnerReceipt();
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
        context.Response.StatusCode = ex.Code == "not_found" ? 404 : ex.Code == "request_queue_full" ? 429 : ex.Code is "settings_conflict" or "stale_revision" or "state_tampered" or "draft_tampered" or "transaction_conflict" or "transaction_closed" or "transaction_recovery_required" ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
    }
    catch (Exception ex) when (ex is System.Xml.XmlException or System.Xml.Schema.XmlSchemaException or System.Xml.XPath.XPathException or ArgumentException or System.Text.Json.JsonException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_request", message = ex.Message }); }
});
app.MapGet("/health", () => new { status = "ok", name = WorkspaceIdentity.Name, version = WorkspaceIdentity.Version,
    packageVersion = WorkspaceIdentity.PackageVersion, assemblyFileVersion = WorkspaceIdentity.AssemblyFileVersion });
app.MapGet("/api/management/activity", (ProjectService service, ExtensionHost extensions) =>
{
    var activity = service.ManagementActivity();
    return new { schema_version = 1, name = WorkspaceIdentity.Name, version = activity.Version,
        active_operations = Math.Clamp(activity.ActiveWorkspaces + activity.QueuedWorkspaces + activity.NativeJobs + activity.PollingWatches + extensions.ManagementActivity(), 0, 1000000),
        process_containment = OwnedProcessContainment.Snapshot(),
        restart_allowed = false, restart_reason = "No atomic restart fence for all chats, unsaved GUI documents, prepared reviews and unresolved native effects. Use the operator-managed update workflow." };
});
app.MapGet("/api/capabilities", (ProjectService service) => service.Capabilities());
app.MapGet("/api/settings", (ServiceSettingsStore settings) => settings.Read());
app.MapPost("/api/settings/broad-local-preview", (ServiceSettingsStore settings, System.Text.Json.JsonElement configuration) => settings.BroadLocalPreview(configuration));
app.MapPost("/api/settings", (ServiceSettingsStore settings, ServiceSettingsSaveRequest request) => settings.Save(request));
app.MapGet("/api/concurrency", (ProjectService service) => service.Concurrency());
app.MapGet("/api/transactions", (ProjectService service, string? status, int? offset, int? limit) => service.Transactions(status, offset ?? 0, limit ?? 50));
app.MapPost("/api/transactions", (ProjectService service, WorkspaceTransactionBeginRequest request, CancellationToken ct) => service.BeginTransactionAsync(request, ct));
app.MapGet("/api/transactions/{id}", (ProjectService service, string id, CancellationToken ct) => service.TransactionAsync(id, ct));
app.MapPost("/api/transactions/{id}/stage", (ProjectService service, string id, WorkspaceTransactionStageRequest request, CancellationToken ct) => service.StageTransactionAsync(id, request, ct));
app.MapPost("/api/transactions/{id}/preview", (ProjectService service, string id, WorkspaceTransactionVersionRequest request, CancellationToken ct) => service.PreviewTransactionAsync(id, request, ct));
app.MapPost("/api/transactions/{id}/commit", (ProjectService service, string id, WorkspaceTransactionCommitRequest request, CancellationToken ct) => service.CommitTransactionAsync(id, request, ct));
app.MapPost("/api/transactions/{id}/abort", (ProjectService service, string id, WorkspaceTransactionVersionRequest request, CancellationToken ct) => service.AbortTransactionAsync(id, request, ct));
app.MapGet("/api/hmi-languages", (ProjectService service) => service.HmiLanguages());
app.MapPost("/api/hmi-languages", (ProjectService service, HmiLanguageRegisterRequest request, CancellationToken ct) => service.RegisterHmiLanguageAsync(request, ct));
app.MapGet("/api/hmi-languages/{id}", (ProjectService service, string id) => service.HmiLanguage(id));
app.MapPost("/api/hmi-languages/{id}/browse", (ProjectService service, string id, HmiLanguageQuery request) => service.BrowseHmiLanguage(id, request));
app.MapPost("/api/hmi-languages/{id}/preview", (ProjectService service, string id, HmiLanguagePreviewRequest request, CancellationToken ct) => service.PreviewHmiLanguageAsync(id, request, ct));
app.MapPost("/api/hmi-languages/{id}/apply", (ProjectService service, string id, HmiLanguageApplyRequest request, CancellationToken ct) => service.ApplyHmiLanguageAsync(id, request, ct));
app.MapPost("/api/hmi-languages/{id}/export", (ProjectService service, string id, HmiLanguageExportRequest request, CancellationToken ct) => service.ExportHmiLanguageAsync(id, request, ct));
app.MapPost("/api/hmi-languages/{id}/verify", (ProjectService service, string id, HmiLanguageVerifyRequest request) => service.VerifyHmiLanguage(id, request));
app.MapPost("/api/projects/{id}/documentation/browse", (ProjectService service, string id, DocumentationQuery request) => service.BrowseDocumentation(id, request));
app.MapPost("/api/projects/{id}/documentation/preview", (ProjectService service, string id, DocumentationPreviewRequest request, CancellationToken ct) => service.PreviewDocumentationAsync(id, request, ct));
app.MapPost("/api/projects/{id}/documentation/apply", (ProjectService service, string id, DocumentationApplyRequest request, CancellationToken ct) => service.ApplyDocumentationAsync(id, request, ct));
app.MapPost("/api/projects/{id}/documentation/export", (ProjectService service, string id, DocumentationExportRequest request, CancellationToken ct) => service.ExportDocumentationAsync(id, request, ct));
app.MapGet("/api/projects", (ProjectService service) => service.Projects());
app.MapPost("/api/projects/compare", (ProjectService service, ProjectCompareRequest request) => service.CompareProjects(request));
app.MapPost("/api/projects/compare/routines", (ProjectService service, ProjectCompareRequest request) => service.CompareRoutines(request));
app.MapPost("/api/projects/compare/ladder", (ProjectService service, LadderCompareRequest request) => service.CompareLadder(request));
app.MapPost("/api/projects/compare/locate", (ProjectService service, LadderLocateRequest request) => service.LocateComparedLadder(request));
app.MapPost("/api/projects/{id}/logic/trace", (ProjectService service, string id, LogicTraceRequest request) => service.TraceLogic(id, request));
app.MapGet("/api/diagnostics/captures", (ProjectService service) => service.DiagnosticCaptures());
app.MapPost("/api/diagnostics/captures", (ProjectService service, DiagnosticCaptureRequest request, CancellationToken ct) => service.CaptureDiagnosticValuesAsync(request, ct));
app.MapPost("/api/diagnostics/evaluate", (ProjectService service, RungEvaluationRequest request) => service.EvaluateCapturedRung(request));
app.MapGet("/api/diagnostics/triggers", (ProjectService service) => service.DiagnosticTriggers());
app.MapPost("/api/diagnostics/triggers", (ProjectService service, DiagnosticTriggerSaveRequest request, CancellationToken ct) => service.SaveDiagnosticTriggerAsync(request, ct));
app.MapPost("/api/diagnostics/triggers/{id}/arm", (ProjectService service, string id, DiagnosticTriggerArmRequest request, CancellationToken ct) => service.ArmDiagnosticTriggerAsync(id, request, ct));
app.MapPost("/api/diagnostics/triggers/{id}/delete", (ProjectService service, string id, ReviewDeleteRequest request) => service.DeleteDiagnosticTrigger(id, request));
app.MapGet("/api/diagnostics/triggers/{id}/episodes/{episodeId}", (ProjectService service, string id, string episodeId) => service.GetDiagnosticEpisode(id, episodeId));
app.MapPost("/api/diagnostics/triggers/{id}/episodes/{episodeId}/capture", (ProjectService service, string id, string episodeId, DiagnosticEpisodeCaptureRequest request, CancellationToken ct) => service.CaptureDiagnosticEpisodeAsync(id, episodeId, request, ct));
app.MapGet("/api/trends", (ProjectService service) => service.SavedTrends());
app.MapGet("/api/trends/{id}", (ProjectService service, string id) => service.GetSavedTrend(id));
app.MapPost("/api/trends", (ProjectService service, SavedTrendRequest request, CancellationToken ct) => service.SaveTrendAsync(request, ct));
app.MapPost("/api/trends/{id}/delete", (ProjectService service, string id, ReviewDeleteRequest request, CancellationToken ct) => service.DeleteSavedReviewAsync("trend-views", id, request, ct));
app.MapPost("/api/trends/analyze", (ProjectService service, TrendAnalyzeRequest request) => service.AnalyzeTrend(request));
app.MapPost("/api/trends/export", (ProjectService service, TrendExportRequest request) => service.ExportTrendAnalysis(request));
app.MapPost("/api/logic-value-merge/preview", (ProjectService service, LogicValueMergeRequest request, CancellationToken ct) => service.PreviewLogicValueMergeAsync(request, ct));
app.MapPost("/api/logic-value-merge/apply", (ProjectService service, LogicValueMergeApplyRequest request, CancellationToken ct) => service.ApplyLogicValueMergeAsync(request, ct));
app.MapPost("/api/legacy-review/preview", (ProjectService service, LegacyReviewRequest request, CancellationToken ct) => service.PreviewLegacyReviewAsync(request, ct));
app.MapPost("/api/legacy-review/prepare", (ProjectService service, LegacyReviewPrepareRequest request, CancellationToken ct) => service.PrepareLegacyReviewAsync(request, ct));
app.MapGet("/api/logix-validation/profiles", (ProjectService service) => service.LogixValidationProfiles());
app.MapPost("/api/controller-tag-rename/preview", (ProjectService service, ControllerTagRenameRequest request, CancellationToken ct) => service.PreviewControllerTagRenameAsync(request, ct));
app.MapPost("/api/controller-tag-rename/apply", (ProjectService service, ControllerTagRenameApplyRequest request, CancellationToken ct) => service.ApplyControllerTagRenameAsync(request, ct));
app.MapPost("/api/module-connections/browse", (ProjectService service, ModuleConnectionBrowseRequest request, CancellationToken ct) => service.BrowseModuleConnectionsAsync(request, ct));
app.MapPost("/api/module-connections/preview", (ProjectService service, ModuleConnectionEditRequest request, CancellationToken ct) => service.PreviewModuleConnectionsAsync(request, ct));
app.MapPost("/api/module-connections/apply", (ProjectService service, ModuleConnectionApplyRequest request, CancellationToken ct) => service.ApplyModuleConnectionsAsync(request, ct));
app.MapPost("/api/structural-edits/context", (ProjectService service, StructuralContextRequest request, CancellationToken ct) => service.StructuralContextAsync(request, ct));
app.MapPost("/api/structural-edits/context/batch", (ProjectService service, StructuralContextRequest request, CancellationToken ct) => service.StructuralContextAsync(request, ct));
app.MapPost("/api/structural-edits/preview", (ProjectService service, StructuralEditRequest request, CancellationToken ct) => service.PreviewStructuralEditsAsync(request, ct));
app.MapPost("/api/structural-edits/apply", (ProjectService service, StructuralApplyRequest request, CancellationToken ct) => service.ApplyStructuralEditsAsync(request, ct));
app.MapGet("/api/structural-edits/previews/{id}", (ProjectService service, string id, string expectedSha256, CancellationToken ct) => service.StructuralEditStatusAsync(id, expectedSha256, ct));
app.MapPost("/api/logix-validation/preview", (ProjectService service, LogixValidationRequest request, CancellationToken ct) => service.PreviewLogixValidationAsync(request, ct));
app.MapPost("/api/logix-validation/record", (ProjectService service, LogixValidationRecordRequest request, CancellationToken ct) => service.RecordLogixValidationAsync(request, ct));
app.MapGet("/api/logix-validation/receipts/{id}", (ProjectService service, string id, string expectedSha256) => service.LogixValidationReceipt(id, expectedSha256));
app.MapPost("/api/version-compatibility/preview", (ProjectService service, VersionCompatibilityRequest request) => service.PreviewVersionCompatibility(request));
app.MapPost("/api/version-compatibility/start", (ProjectService service, VersionCompatibilityStartRequest request, CancellationToken ct) => service.StartVersionCompatibilityAsync(request, ct));
app.MapGet("/api/version-compatibility/{id}", (ProjectService service, string id) => service.VersionCompatibilityStatus(id));
app.MapPost("/api/version-compatibility/{id}/readback", (ProjectService service, string id, CancellationToken ct) => service.StartVersionReadbackAsync(id, ct));
app.MapPost("/api/comparison-reviews/export", (ProjectService service, ComparisonReviewExportRequest request, CancellationToken ct) => service.ExportComparisonReviewAsync(request, ct));
app.MapGet("/api/comparison-reviews", (ProjectService service, string? query, int? offset, int? limit, CancellationToken ct) => service.ComparisonReviewsAsync(query, offset ?? 0, limit ?? 50, ct));
app.MapGet("/api/comparison-reviews/{id}", (ProjectService service, string id, CancellationToken ct) => service.ComparisonReviewAsync(id, ct));
app.MapPost("/api/comparison-reviews/{id}/forget", (ProjectService service, string id, ComparisonReviewDeleteRequest request, CancellationToken ct) => service.ForgetComparisonReviewAsync(id, request, ct));
app.MapGet("/api/comparison-reviews/{id}/annotations", (ProjectService service, string id, string? query, string? status, int? offset, int? limit, CancellationToken ct) => service.ComparisonAnnotationsAsync(id, query, status, offset ?? 0, limit ?? 50, ct));
app.MapPost("/api/comparison-reviews/{id}/annotations", (ProjectService service, string id, ComparisonAnnotationUpdateRequest request, CancellationToken ct) => service.UpdateComparisonAnnotationsAsync(id, request, ct));
app.MapPost("/api/comparison-reviews/{id}/annotations/export", (ProjectService service, string id, ComparisonAnnotationExportRequest request, CancellationToken ct) => service.ExportComparisonAnnotationsAsync(id, request, ct));
app.MapPost("/api/value-merge/capture", (ProjectService service, StructurePullRequest request, CancellationToken ct) => service.CaptureTagValueSourceAsync(request, ct));
app.MapPost("/api/value-merge/preview", (ProjectService service, TagValueMergeRequest request, CancellationToken ct) => service.PreviewTagValueMergeAsync(request, ct));
app.MapPost("/api/value-merge/browse", (ProjectService service, TagValueMergeBrowseRequest request, CancellationToken ct) => service.BrowseTagValueMergeAsync(request, ct));
app.MapPost("/api/value-merge/apply", (ProjectService service, TagValueMergeApplyRequest request, CancellationToken ct) => service.ApplyTagValueMergeAsync(request, ct));
app.MapGet("/api/comparison-sessions", (ProjectService service, CancellationToken ct) => service.SavedReviewsAsync("comparison-sessions", ct));
app.MapGet("/api/comparison-sessions/{id}", (ProjectService service, string id, CancellationToken ct) => service.GetComparisonSessionAsync(id, ct));
app.MapPost("/api/comparison-sessions", (ProjectService service, ComparisonSessionSaveRequest request, CancellationToken ct) => service.SaveComparisonSessionAsync(request, ct));
app.MapPost("/api/comparison-sessions/{id}/delete", (ProjectService service, string id, ReviewDeleteRequest request, CancellationToken ct) => service.DeleteSavedReviewAsync("comparison-sessions", id, request, ct));
app.MapPost("/api/comparison-sessions/recovery/preview", (ProjectService service, ComparisonSessionRecoveryRequest request, CancellationToken ct) => service.PreviewComparisonRecoveryAsync(request, ct));
app.MapPost("/api/comparison-sessions/recovery/apply", (ProjectService service, ComparisonSessionRecoveryApplyRequest request, CancellationToken ct) => service.ApplyComparisonRecoveryAsync(request, ct));
app.MapGet("/api/merge-mappings", (ProjectService service, CancellationToken ct) => service.SavedReviewsAsync("merge-mappings", ct));
app.MapGet("/api/merge-mappings/{id}", (ProjectService service, string id, CancellationToken ct) => service.GetMergeMappingAsync(id, ct));
app.MapPost("/api/merge-mappings", (ProjectService service, MergeMappingSaveRequest request, CancellationToken ct) => service.SaveMergeMappingAsync(request, ct));
app.MapPost("/api/merge-mappings/bind", (ProjectService service, MergeMappingBindRequest request, CancellationToken ct) => service.BindMergeMappingAsync(request, ct));
app.MapPost("/api/merge-mappings/import", (ProjectService service, MergeMappingImportRequest request, CancellationToken ct) => service.ImportMergeMappingAsync(request, ct));
app.MapPost("/api/merge-mappings/{id}/export", (ProjectService service, string id, MergeMappingExportRequest request, CancellationToken ct) => service.ExportMergeMappingAsync(id, request, ct));
app.MapPost("/api/merge-mappings/{id}/delete", (ProjectService service, string id, ReviewDeleteRequest request, CancellationToken ct) => service.DeleteSavedReviewAsync("merge-mappings", id, request, ct));
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
app.MapGet("/api/sdk/service", (ProjectService service) => service.SdkServiceStatus());
app.MapPost("/api/sdk/service/ensure-running", (ProjectService service, SdkServiceEnsureRequest request, CancellationToken ct) => service.EnsureSdkServiceRunningAsync(request, ct));
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
app.MapGet("/api/broker/capabilities", async (ExtensionHost host, CancellationToken ct) => AppPublication.Catalog(await host.CatalogAsync(ct)));
app.MapGet("/api/broker/workflow", (string workflowId, string expectedRevision) => AppPublication.Workflow(workflowId, expectedRevision));
app.MapGet("/api/broker/reference", (string workflowId, string expectedRevision, string referenceId, string expectedSha256) => AppPublication.Reference(workflowId, expectedRevision, referenceId, expectedSha256));
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

sealed class SupervisorExitService(ParentProcessOwners owner, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => owner.RequestShutdownOnExitAsync(lifetime.StopApplication, stoppingToken);
}

sealed class OwnerStopService(string root, ResourceSupervisor resources, string key, ProjectService projects,
    ExtensionHost extensions, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mailbox = new OwnerShutdownMailbox(root, resources.Owner.OwnerId, key);
        var reportingFailure = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
            if (await mailbox.PollAsync(() =>
            {
                if (!projects.TryFenceShutdown()) return Task.FromResult(new OwnerShutdownDecision(false, "Finish active mutations, native jobs and polling before shutdown."));
                if (!extensions.TryFenceShutdown()) { projects.ReleaseShutdownFence(); return Task.FromResult(new OwnerShutdownDecision(false, "Extension calls/reloads/jobs are still active.")); }
                if (resources.ShutdownBlockers().Length != 0)
                {
                    extensions.ReleaseShutdownFence(); projects.ReleaseShutdownFence();
                    return Task.FromResult(new OwnerShutdownDecision(false, "Native connections, dirty/unknown documents or shared leases require app-owned close/recovery before shutdown."));
                }
                try { resources.CloseOwnedResources(); return Task.FromResult(new OwnerShutdownDecision(true, "Owned resource cleanup verified; service shutdown requested.")); }
                catch { /* Keep all admission fences after uncertain teardown. */ throw; }
            }, stoppingToken)) { lifetime.StopApplication(); return; }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // An unavailable mailbox/journal must not trigger the host's
                // default background-failure shutdown of native resources.
                if (!reportingFailure) { Console.Error.WriteLine("Owner shutdown reporting is unavailable; preserve the same intent and native state for inspection."); reportingFailure = true; }
            }
            await Task.Delay(100, stoppingToken);
        }
    }
}

sealed class ResourceRefreshService(ResourceSupervisor resources) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reported = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { resources.Refresh(); }
            catch (Exception)
            {
                // Reporting/identity uncertainty cannot unwind a native owner.
                if (!reported) { Console.Error.WriteLine("Resource membership reporting is unavailable; retain the ledger and inspect before cleanup."); reported = true; }
            }
            await Task.Delay(2000, stoppingToken);
        }
    }
}

public partial class Program { }
