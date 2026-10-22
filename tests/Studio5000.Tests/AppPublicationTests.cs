using System.Text.Json;
using Studio5000.Core;
using Studio5000.Extensibility;
using Xunit;

namespace Studio5000.Tests;

public sealed class AppPublicationTests
{
    private static ExtensionCatalog Fixture(string generation = "generation-1", int activeCalls = 0) => new(1,
        [new("logix", "fixture", generation, "hash", [new("inspect", "Inspect PLC metadata", "read", default)], activeCalls, "active")], []);

    [Fact]
    public void Catalog_is_normalized_stable_and_only_references_discovery()
    {
        var catalog = AppPublication.Catalog(Fixture());
        Assert.Equal(1, catalog.GetProperty("schema_version").GetInt32());
        Assert.Equal(catalog.GetProperty("catalog_revision").GetString(), AppPublication.Catalog(Fixture(activeCalls: 7)).GetProperty("catalog_revision").GetString());
        Assert.NotEqual(catalog.GetProperty("catalog_revision").GetString(), AppPublication.Catalog(Fixture("generation-2")).GetProperty("catalog_revision").GetString());
        var operation = catalog.GetProperty("capabilities")[0];
        Assert.Equal("logix.inspect", operation.GetProperty("id").GetString());
        Assert.Equal("studio_extensions_describe", operation.GetProperty("discovery").GetProperty("tool").GetString());
        Assert.Equal("generation-1", operation.GetProperty("discovery").GetProperty("arguments").GetProperty("generation").GetString());
        Assert.DoesNotContain("sourcePath", catalog.GetRawText());
        Assert.DoesNotContain("apply", operation.GetRawText());
    }

    [Fact]
    public void Guides_share_skill_content_and_pin_all_references()
    {
        var catalog = AppPublication.Catalog(Fixture());
        foreach (var entry in catalog.GetProperty("capabilities").EnumerateArray().Where(c => c.GetProperty("id").GetString()!.StartsWith("workflow.")))
        {
            var arguments = entry.GetProperty("discovery").GetProperty("arguments");
            var id = arguments.GetProperty("workflowId").GetString()!; var revision = arguments.GetProperty("expectedRevision").GetString()!;
            var guide = AppPublication.Workflow(id, revision);
            var instructions = guide.GetProperty("instructions"); var text = instructions.GetProperty("text").GetString()!;
            Assert.Contains("Do not install plugins", text);
            Assert.DoesNotContain("Universal Tools", text);
            Assert.DoesNotContain("broker", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ProjectService.Hash(System.Text.Encoding.UTF8.GetBytes(text)).ToLowerInvariant(), instructions.GetProperty("sha256").GetString());
            var source = guide.GetProperty("skill_source"); var sourceText = source.GetProperty("text").GetString()!;
            Assert.Equal("SKILL.md", source.GetProperty("path").GetString());
            Assert.StartsWith("---", sourceText);
            Assert.Contains("name: " + id, sourceText);
            Assert.EndsWith(sourceText, text);
            Assert.Equal(ProjectService.Hash(System.Text.Encoding.UTF8.GetBytes(sourceText)).ToLowerInvariant(), source.GetProperty("sha256").GetString());
            Assert.Throws<InvalidOperationException>(() => AppPublication.Workflow(id, "stale"));
            Assert.Throws<ArgumentException>(() => AppPublication.Reference(id, revision, "../../secrets", "hash"));
            foreach (var reference in guide.GetProperty("references").EnumerateArray())
            {
                var referenceId = reference.GetProperty("id").GetString()!; var hash = reference.GetProperty("sha256").GetString()!;
                var result = AppPublication.Reference(id, revision, referenceId, hash);
                Assert.Equal(hash, ProjectService.Hash(System.Text.Encoding.UTF8.GetBytes(result.GetProperty("content").GetString()!)).ToLowerInvariant());
                Assert.Throws<InvalidOperationException>(() => AppPublication.Reference(id, revision, referenceId, "stale"));
                Assert.Throws<InvalidOperationException>(() => AppPublication.Reference(id, "stale", referenceId, hash));
            }
        }
        Assert.Throws<ArgumentException>(() => AppPublication.Workflow("unknown", "revision"));
    }

    [Fact]
    public void Doctor_reports_selected_route_and_concrete_gaps_without_native_discovery()
    {
        var args = AppPublication.Catalog(Fixture()).GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "protocol.features").GetProperty("discovery").GetProperty("arguments");
        var doctor = AppPublication.Workflow(args.GetProperty("workflowId").GetString()!, args.GetProperty("expectedRevision").GetString()!);
        Assert.Equal("standalone-dotnet", doctor.GetProperty("profile").GetString());
        var features = doctor.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(12, features.Length);
        Assert.Equal("blocked", features.Single(f => f.GetProperty("id").GetString() == "tasks").GetProperty("state").GetString());
        Assert.Contains("no Skills handlers", features.Single(f => f.GetProperty("id").GetString() == "skills-over-mcp").GetProperty("reason").GetString());
        Assert.DoesNotContain("tokenFile", doctor.GetRawText());
        Assert.Throws<InvalidOperationException>(() => AppPublication.Workflow("protocol-features", "stale"));
    }

    [Fact]
    public void Incomplete_or_oversize_metadata_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => AppPublication.Catalog(Fixture() with { LoadErrors = ["private local path"] }));
        var large = Fixture(); large = large with { Extensions = [large.Extensions[0] with { Operations = Enumerable.Range(0, 1000).Select(i => new ExtensionOperation("op" + i, "Description", "read", default)).ToArray() }] };
        Assert.Throws<InvalidOperationException>(() => AppPublication.Catalog(large));
    }
}
