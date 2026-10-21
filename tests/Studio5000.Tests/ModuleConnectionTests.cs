using System.Text;
using System.Text.Json;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ModuleConnectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellModuleConnections", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public ModuleConnectionTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private static string Source(int count = 1) => "<?xml version='1.0' encoding='utf-8'?>\r\n<!-- unrelated μ 🚀 -->\n<RSLogix5000Content><Controller Name='Demo'><SafetyInfo Signature='retain'/><Modules>" +
        string.Concat(Enumerable.Range(0, count).Select(i => "<Module Name='DemoModule" + i + "' CatalogNumber='Generated' SafetyEnabled='true' SafetyNetwork='retain'><Communications><ConfigTag><Data><![CDATA[opaque 10000 200 60.032\r\n]]></Data></ConfigTag>\r\n<Connections><Connection Name='Input' Type='SafetyInput' RPI = '10000' NetworkDelayMultiplier=\"200\" ReactionTimeLimit='60.032' TimeoutMultiplier='2' Unicast='true'><InputTag><Data><![CDATA[retained 10000]]></Data><ForceData><![CDATA[retain 200]]></ForceData></InputTag></Connection><RackConnection Name='Unsupported' RPI='10000'/></Connections></Communications></Module>\n")) +
        "</Modules><Tags><Tag Name='Value'><Data><![CDATA[10000]]></Data><Description><![CDATA[keep 60.032]]></Description></Tag></Tags></Controller></RSLogix5000Content>\r\n";
    private async Task<ProjectInfo> Register(string text, Encoding? encoding = null)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, text, encoding ?? new UTF8Encoding(true)); return await service.RegisterAsync(path); }
    private byte[] Revision(ProjectInfo p) => File.ReadAllBytes(Path.Combine(root, "state", "projects", p.Id, "revision-" + p.Revision + ".l5x"));
    private static ModuleConnectionEdit Edit(string field = "RPI", string before = "10000", string after = "20000", string module = "DemoModule0") => new(module, "Input", field, before, after);
    private static ModuleConnectionEditRequest Request(ProjectInfo p, params ModuleConnectionEdit[] edits) => new(p.Id, p.CurrentSha256, edits, "review-only");
    [Theory] [InlineData("utf-8")] [InlineData("utf-16")] [InlineData("utf-32")]
    public async Task Selected_spans_preserve_all_other_bytes_payloads_forces_and_signatures(string name)
    {
        var encoding = Encoding.GetEncoding(name); var text = Source().Replace("encoding='utf-8'", "encoding='" + name + "'"); var p = await Register(text, encoding); var original = Revision(p);
        var request = Request(p, Edit(), Edit("NetworkDelayMultiplier", "200", "100"), Edit("ReactionTimeLimit", "60.032", "40.032"));
        var plan = await service.PreviewModuleConnectionsAsync(request); Assert.True(plan.CanApply); Assert.Equal("scalar-byte-spans-v1", plan.XmlPreservation);
        Assert.All(plan.Changes, c => { Assert.True(c.Changed); Assert.Equal("10000", c.Before.Timing["RPI"]); Assert.Equal("200", c.Before.Timing["NetworkDelayMultiplier"]); Assert.Equal("60.032", c.Before.Timing["ReactionTimeLimit"]); Assert.Equal("20000", c.After.Timing["RPI"]); Assert.Equal("100", c.After.Timing["NetworkDelayMultiplier"]); Assert.Equal("40.032", c.After.Timing["ReactionTimeLimit"]); });
        var result = await service.ApplyModuleConnectionsAsync(new(request, plan.ReviewSha256)); var expected = text.Replace("RPI = '10000'", "RPI = '20000'").Replace("NetworkDelayMultiplier=\"200\"", "NetworkDelayMultiplier=\"100\"").Replace("ReactionTimeLimit='60.032'", "ReactionTimeLimit='40.032'");
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)), Revision(result.Project)); Assert.Equal(original, Revision(p)); Assert.Equal(original, File.ReadAllBytes(p.SourcePath)); Assert.NotEqual(p.Id, result.Project.Id);
        Assert.True(result.ReviewOnly); Assert.Equal("not-performed", result.NativeAcceptance); Assert.StartsWith("REVIEW ONLY", result.Project.Name); Assert.Equal(plan.ResultSha256, result.Project.CurrentSha256);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(service.Download(result.Receipt.Id).Path)); Assert.Equal(plan.ReviewSha256, receipt.RootElement.GetProperty("plan").GetProperty("reviewSha256").GetString());
    }
    [Fact]
    public async Task Forty_explicit_attributes_are_reviewed_without_implicit_timing_recalculation()
    {
        var p = await Register(Source(20)); var edits = Enumerable.Range(0, 20).SelectMany(i => new[] { Edit(module: "DemoModule" + i), Edit("NetworkDelayMultiplier", "200", "100", "DemoModule" + i) }).ToArray(); var request = Request(p, edits);
        var plan = await service.PreviewModuleConnectionsAsync(request); Assert.Equal(40, plan.Changes.Length); Assert.All(plan.Changes, c => Assert.Equal("60.032", c.After.Timing["ReactionTimeLimit"]));
        var result = await service.ApplyModuleConnectionsAsync(new(request, plan.ReviewSha256)); Assert.Equal(40, plan.Changes.Count(c => c.Changed)); Assert.Equal(Revision(p).Length, Revision(result.Project).Length);
    }
    [Fact]
    public async Task Browse_is_hash_bound_paged_and_reports_safety_context_and_unsupported_connection_elements()
    {
        var p = await Register(Source(3)); var result = await service.BrowseModuleConnectionsAsync(new(p.Id, p.CurrentSha256, "DemoModule", 1, 1)); Assert.Equal(3, result.Total); Assert.Equal(3, result.Matching); Assert.Equal(3, result.UnsupportedConnectionElements);
        var connection = Assert.Single(result.Connections); Assert.Equal("DemoModule1", connection.ModuleName); Assert.Contains(connection.SafetyEvidence, s => s.Contains("SafetyInput")); Assert.Equal("2", connection.Timing["TimeoutMultiplier"]);
        await Assert.ThrowsAsync<OperationException>(() => service.BrowseModuleConnectionsAsync(new(p.Id, "wrong")));
        await Assert.ThrowsAsync<OperationException>(() => service.BrowseModuleConnectionsAsync(new(p.Id, p.CurrentSha256, Limit: 257)));
    }
    [Fact]
    public async Task Safety_defaults_to_block_and_policy_or_before_value_drift_invalidates_apply()
    {
        var p = await Register(Source()); var blockedRequest = Request(p, Edit()) with { SafetyHandling = "block" }; var blocked = await service.PreviewModuleConnectionsAsync(blockedRequest); Assert.False(blocked.CanApply); Assert.Null(blocked.ResultSha256);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyModuleConnectionsAsync(new(blockedRequest, blocked.ReviewSha256)));
        var request = blockedRequest with { SafetyHandling = "review-only" }; var plan = await service.PreviewModuleConnectionsAsync(request);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyModuleConnectionsAsync(new(request, blocked.ReviewSha256)));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewModuleConnectionsAsync(request with { Edits = [Edit(before: "999")] }));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewModuleConnectionsAsync(request with { ExpectedSha256 = new string('0', 64) }));
        var ordinary = await Register(Source().Replace("SafetyEnabled='true'", "SafetyEnabled='false'").Replace(" SafetyNetwork='retain'", "").Replace("SafetyInput", "Input").Replace(" NetworkDelayMultiplier=\"200\" ReactionTimeLimit='60.032' TimeoutMultiplier='2'", ""));
        Assert.True((await service.PreviewModuleConnectionsAsync(Request(ordinary, Edit()) with { SafetyHandling = "block" })).CanApply);
        Assert.Contains(plan.Findings, f => f.Contains("not recalculated"));
    }
    [Theory]
    [InlineData("RPI", "0")] [InlineData("RPI", "1.5")] [InlineData("RPI", "4294967296")] [InlineData("RPI", "1e4")] [InlineData("RPI", " 10000")]
    [InlineData("NetworkDelayMultiplier", "9")] [InlineData("NetworkDelayMultiplier", "601")] [InlineData("NetworkDelayMultiplier", "100.5")]
    [InlineData("ReactionTimeLimit", "-1")] [InlineData("ReactionTimeLimit", "5500032.001")] [InlineData("ReactionTimeLimit", "40,032")]
    [InlineData("Name", "new")] [InlineData("TimeoutMultiplier", "3")] [InlineData("Unicast", "false")]
    public async Task Unreviewed_fields_and_invalid_numeric_shapes_are_rejected(string field, string value)
    { var p = await Register(Source()); Assert.Equal("module_connection_policy", (await Assert.ThrowsAsync<OperationException>(() => service.PreviewModuleConnectionsAsync(Request(p, Edit(field, "10000", value))))).Code); }
    [Fact]
    public async Task Missing_ambiguous_and_duplicate_targets_fail_without_broadening_general_XML_edits()
    {
        var p = await Register(Source());
        foreach (var edit in new[] { Edit(module: "missing"), Edit() with { ConnectionName = "missing" }, Edit() with { ConnectionName = "Unsupported" }, Edit() with { ModuleName = "DemoModule0' or true()" } })
            await Assert.ThrowsAsync<OperationException>(() => service.PreviewModuleConnectionsAsync(Request(p, edit)));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewModuleConnectionsAsync(Request(p, Edit(), Edit())));
        var missing = await Register(Source().Replace(" RPI = '10000'", "")); await Assert.ThrowsAsync<OperationException>(() => service.PreviewModuleConnectionsAsync(Request(missing, Edit())));
        var duplicate = await Register(Source().Replace("<RackConnection", "<Connection" ).Replace("Name='Unsupported'", "Name='Input'")); await Assert.ThrowsAsync<OperationException>(() => service.BrowseModuleConnectionsAsync(new(duplicate.Id, duplicate.CurrentSha256)));
        Assert.Equal("invalid_target", Assert.Throws<OperationException>(() => XmlProjects.Apply(Revision(p), [new("set_attribute", "/RSLogix5000Content/Controller/Modules/Module/Communications/Connections/Connection", "20000", "RPI")])).Code);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
