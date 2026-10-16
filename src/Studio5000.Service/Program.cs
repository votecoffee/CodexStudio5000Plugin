using System.Net;
using System.Security.Cryptography;
using System.Text;
using Studio5000.Core;

var root = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = root });
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
var local = Path.Combine(root, ".local");
Directory.CreateDirectory(local);
ProjectService.RejectLinks(local);
var tokenPath = Path.Combine(local, "service.key");
if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token = File.ReadAllText(tokenPath).Trim();
if (token.Length < 32) throw new InvalidOperationException("service.key is too short.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ProjectService>();
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
    catch (Exception ex) when (ex is System.Xml.XmlException or System.Xml.XPath.XPathException or ArgumentException or System.Text.Json.JsonException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_request", message = ex.Message }); }
});
app.MapGet("/health", () => new { status = "ok", version = "0.1.0" });
app.MapGet("/api/capabilities", (ProjectService service) => service.Capabilities());
app.MapGet("/api/projects", (ProjectService service) => service.Projects());
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
await app.RunAsync();

public partial class Program { }
