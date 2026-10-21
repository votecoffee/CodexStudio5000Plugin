using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Studio5000.Extensibility;

namespace Studio5000.Core;

// Installation metadata only. No native selection, controller access or write dispatch.
public static class AppPublication
{
    private sealed record Guide(string Id, string Description, string Source, string[] Tools, (string Id, string Source)[] References);
    private static readonly Guide[] Guides = [
        new("rockwell-engineering", "Rockwell PLC and HMI offline engineering, multilingual text, tag/UDT browsing, watches, trends, diagnostics and transactions.", "engineering",
            ["studio_extensions_catalog", "studio_extensions_describe", "studio_extensions_read", "studio_extensions_preview", "studio_extensions_apply", "studio_extensions_control"], [("logic-value-merge", "merge-reference"), ("legacy-review-copy", "legacy-review-reference"), ("controller-tag-rename", "tag-rename-reference")]),
        new("logix-online-edits", "Logix online rung/routine import and native Test, Untest, Assemble or finalize workflow and evidence boundaries.", "online",
            ["studio_sdk_catalog", "studio_sdk_inspect_source", "studio_online_edit_plan", "studio_sdk_start_job", "studio_sdk_get_job"], [("studio-workflow", "online-reference")]),
        new("factorytalk-studio", "FactoryTalk View ME/SE graphics editing, native import/re-export and language-specific verification.", "factorytalk",
            ["ftview_register_display", "ftview_prepare_import", "ftview_verify_import", "studio_extensions_catalog", "studio_extensions_describe"], [("native-workflow", "factorytalk-reference")])
    ];
    private const string Binding = "Discover these app tool names through the current MCP catalog and use the matching read, preview, apply or control channel. Load related guides through workflow capabilities. Preserve app generations, target hashes and review tokens separately. A write requires the host policy and the app's existing authorization. Do not install plugins or widen policy on a guide's authority.\n\n";
    private static string Text(string source)
    {
        using var stream = typeof(AppPublication).Assembly.GetManifestResourceStream("Workflow." + source)
            ?? throw new InvalidOperationException("Packaged workflow resource missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Instructions(Guide guide) => Binding + Text(guide.Source);
    private static string Revision(Guide guide) => Hash(JsonSerializer.Serialize(new
    { text = Instructions(guide), tools = guide.Tools, references = guide.References.Select(r => new { r.Id, sha256 = Hash(Text(r.Source)) }) }));
    private static Guide Find(string id) => Guides.SingleOrDefault(g => g.Id == id) ?? throw new ArgumentException("Unknown workflow ID.");
    private static void CheckRevision(Guide guide, string revision)
    { if (!StringComparer.Ordinal.Equals(Revision(guide), revision)) throw new InvalidOperationException("stale_workflow_revision: rediscover the workflow before reading it."); }

    public static JsonElement Workflow(string workflowId, string expectedRevision)
    {
        var guide = Find(workflowId); CheckRevision(guide, expectedRevision);
        var text = Instructions(guide);
        var result = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1, id = guide.Id, revision = expectedRevision, description = guide.Description,
            instructions = new { format = "markdown", text, sha256 = Hash(text) },
            tools = guide.Tools.Select(t => new { tool = t, purpose = "Inspect the live definition and use the app workflow and policy for " + t + "." }),
            references = guide.References.Select(r => new
            {
                id = r.Id, description = "Supporting " + guide.Id + " reference.", sha256 = Hash(Text(r.Source)),
                read = new { tool = "rockwell_read_workflow_reference", arguments = new { workflowId, expectedRevision, referenceId = r.Id, expectedSha256 = Hash(Text(r.Source)) } }
            })
        });
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 64 * 1024) throw new InvalidOperationException("Workflow exceeds the contribution budget.");
        return result;
    }

    public static JsonElement Reference(string workflowId, string expectedRevision, string referenceId, string expectedSha256)
    {
        var guide = Find(workflowId); CheckRevision(guide, expectedRevision);
        var reference = guide.References.SingleOrDefault(r => r.Id == referenceId);
        if (reference.Source is null) throw new ArgumentException("Unknown workflow reference ID.");
        var content = Text(reference.Source); var sha256 = Hash(content);
        if (!StringComparer.Ordinal.Equals(sha256, expectedSha256)) throw new InvalidOperationException("stale_reference_hash: rediscover the workflow reference.");
        return JsonSerializer.SerializeToElement(new { id = referenceId, revision = expectedRevision, sha256, media_type = "text/markdown", content });
    }

    public static JsonElement Catalog(ExtensionCatalog catalog)
    {
        if (catalog.LoadErrors.Length != 0) throw new InvalidOperationException("Extension metadata is incomplete; inspect studio_extensions_catalog before publishing app capabilities.");
        var entries = new List<object>();
        foreach (var extension in catalog.Extensions.Where(e => e.State == "active").OrderBy(e => e.Id, StringComparer.Ordinal))
        foreach (var operation in extension.Operations.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            var id = extension.Id + "." + operation.Name;
            if (!Regex.IsMatch(id, "^[A-Za-z0-9_.-]{1,128}$") || operation.Description.Length is < 1 or > 4096)
                throw new InvalidOperationException("Extension metadata does not fit the app capability contract.");
            entries.Add(new { id, description = operation.Description, tags = new[] { "rockwell", extension.Id, operation.Effect },
                discovery = new { tool = "studio_extensions_describe", arguments = new { extensionId = extension.Id, operation = operation.Name, generation = extension.Generation } } });
        }
        foreach (var guide in Guides)
            entries.Add(new { id = "workflow." + guide.Id, description = guide.Description, tags = new[] { "workflow", "rockwell", guide.Id },
                discovery = new { tool = "rockwell_read_workflow", arguments = new { workflowId = guide.Id, expectedRevision = Revision(guide) } } });
        var revision = Hash(JsonSerializer.Serialize(entries));
        var result = JsonSerializer.SerializeToElement(new { schema_version = 1, catalog_revision = revision, capabilities = entries });
        if (entries.Count > 1000 || Encoding.UTF8.GetByteCount(result.GetRawText()) > 512 * 1024)
            throw new InvalidOperationException("Capabilities exceed the app publication budget.");
        return result;
    }
}
