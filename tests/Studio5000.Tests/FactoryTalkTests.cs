using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class FactoryTalkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "CodexFactoryTalkTests", Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options;
    private ProjectService service;
    // An authored unit-test schema, deliberately not a redistributed Rockwell schema.
    private const string TestSchema = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
        <xs:element name="gfx"><xs:complexType><xs:sequence>
        <xs:element name="text" minOccurs="0" maxOccurs="unbounded"><xs:complexType>
        <xs:attribute name="name" type="xs:string" use="required"/>
        <xs:attribute name="caption" type="xs:string" use="required"/>
        <xs:attribute name="width" type="xs:positiveInteger"/>
        </xs:complexType></xs:element>
        <xs:element name="numericDisplay" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
        <xs:element name="connections"><xs:complexType><xs:sequence><xs:element name="connection"><xs:complexType>
        <xs:attribute name="name" type="xs:string"/><xs:attribute name="expression" type="xs:string"/>
        </xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element>
        </xs:sequence><xs:attribute name="name" type="xs:string"/></xs:complexType></xs:element>
        </xs:sequence></xs:complexType></xs:element></xs:schema>
        """;
    private const string ObjectPath = "/gfx[1]/text[@name='Title']";
    private string Source(string edition = "ME")
    {
        var file = Path.Combine(root, "Display-" + edition + ".xml");
        File.WriteAllText(file, """
            <?xml version="1.0" encoding="utf-8"?>
            <gfx xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="Gfx-EDITION1.xsd">
            <text name="Title" caption="Original" width="100"/>
            <text name="Other" caption="Leave unchanged" width="100"/>
            <numericDisplay name="Counter"><connections><connection name="Value" expression="{[PLC]Old.Count}"/></connections></numericDisplay>
            </gfx>
            """.Replace("EDITION", edition));
        return file;
    }
    public FactoryTalkTests()
    {
        Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(root, "schemas"));
        foreach (var edition in new[] { "ME", "SE" }) File.WriteAllText(Path.Combine(root, "schemas", $"Gfx-{edition}1.xsd"), TestSchema);
        options = new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [Path.Combine(root, "exports")],
            FactoryTalkSchemaDirectory = Path.Combine(root, "schemas"), FactoryTalkStudioPath = Path.Combine(root, "not-installed.exe") };
        service = new(options);
    }

    [Theory]
    [InlineData("ME", "ftview-me")]
    [InlineData("SE", "ftview-se")]
    public async Task BothEditionsPreserveSourceAndPersistPropertyEdits(string edition, string kind)
    {
        var source = Source(edition); var original = File.ReadAllBytes(source);
        var p = await service.RegisterAsync(source); Assert.Equal(kind, p.Kind);
        Assert.Contains(service.Inspect(p.Id, query: "Title").Elements, e => e.XPath == ObjectPath);
        var preview = await service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_attribute", ObjectPath, "Updated 零\nSecond line", "caption"),
            new("set_attribute", "//connection[@name='Value']", "{[PLC]New.Count}", "expression")]));
        Assert.Equal(0, service.Get(p.Id).Revision);
        p = await service.ApplyAsync(p.Id, preview.Id, new(p.CurrentSha256));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal("Updated 零\nSecond line", XElement.Parse(service.ReadElement(p.Id, ObjectPath).Xml).Attribute("caption")!.Value);
        var artifact = await service.ExportAsync(p.Id, new(p.CurrentSha256, "xml"));
        Assert.Equal(p.CurrentSha256, artifact.Sha256);
        Assert.Equal("xml", artifact.Format);
        service.Dispose(); service = new(options);
        Assert.Equal(p, service.Get(p.Id)); Assert.True(service.ValidateGraphics(p.Id).SchemaValidated);
    }

    [Fact]
    public async Task MissingSchemaAllowsInspectionButRejectsEdits()
    {
        options.FactoryTalkSchemaDirectory = Path.Combine(root, "absent");
        var p = await service.RegisterAsync(Source());
        Assert.False(service.ValidateGraphics(p.Id).SchemaValidated);
        var error = await Assert.ThrowsAsync<OperationException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_attribute", ObjectPath, "changed", "caption")])));
        Assert.Equal("schema_unavailable", error.Code);
    }

    [Fact]
    public async Task XsdFailureIdentityChangesAndObjectDeletionAreRejected()
    {
        var p = await service.RegisterAsync(Source());
        var bad = await Assert.ThrowsAsync<OperationException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_attribute", ObjectPath, "-1", "width")])));
        Assert.Equal("graphics_schema_invalid", bad.Code);
        var rename = await Assert.ThrowsAsync<OperationException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_attribute", ObjectPath, "NewName", "name")])));
        Assert.Equal("invalid_attribute", rename.Code);
        var remove = await Assert.ThrowsAsync<OperationException>(() => service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("remove_element", ObjectPath)])));
        Assert.Equal("unsupported_graphics_edit", remove.Code);
        Assert.Equal(p, service.Get(p.Id));
    }

    [Theory]
    [InlineData("https://example.org/evil.xsd")]
    [InlineData("../Gfx-ME1.xsd")]
    [InlineData("C:\\Gfx-ME1.xsd")]
    public void ExternalSchemaReferencesAreRejected(string schema)
    {
        var xml = $"<gfx xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='{schema}'/>";
        Assert.Equal("unsupported_schema", Assert.Throws<OperationException>(() => FactoryTalkGraphics.Parse(Encoding.UTF8.GetBytes(xml))).Code);
    }

    [Fact]
    public async Task ImportPlanBindsTargetsAndMatchingFileDoesNotClaimNativeAcceptance()
    {
        var p = await Changed();
        var plan = await service.PrepareGraphicsImportAsync(p.Id, new(p.CurrentSha256, "Fixture Application", "Main Screen", "fr-FR"));
        Assert.Equal("pending", plan.NativeImportStatus); Assert.Single(plan.Changes);
        var batch = XDocument.Load(plan.BatchPath);
        Assert.Equal("update", (string?)batch.Root!.Element("import")!.Attribute("importType"));
        Assert.Equal("Main Screen", (string?)batch.Root.Element("import")!.Attribute("display"));
        using var zip = ZipFile.OpenRead(service.Download(plan.Package.Id).Path);
        Assert.Contains(zip.Entries, e => e.FullName.Replace('\\', '/') == "original-export/Main Screen.xml");
        File.Copy(plan.XmlPath, plan.ReexportPath);
        var result = await service.VerifyGraphicsImportAsync(plan.Id, new());
        Assert.Equal("property_values_match", result.Status);
        Assert.Equal("not_observed_by_service", result.NativeImportStatus);
        Assert.Contains("does not authenticate", result.ContextBoundary);
        var doc = XDocument.Load(plan.ReexportPath); doc.Root!.Elements("text").First().SetAttributeValue("caption", "Wrong"); doc.Save(plan.ReexportPath);
        Assert.Equal("property_values_mismatch", (await service.VerifyGraphicsImportAsync(plan.Id, new())).Status);
    }

    [Fact]
    public async Task VerificationUsesNamedSelectorsWhenNativeObjectOrderChanges()
    {
        var p = await Changed(); var plan = await service.PrepareGraphicsImportAsync(p.Id, new(p.CurrentSha256, "App", "Main", "en-US"));
        var doc = XDocument.Load(plan.XmlPath); var other = doc.Root!.Elements("text").Last(); other.Remove(); doc.Root.AddFirst(other); doc.Save(plan.ReexportPath);
        Assert.Equal("property_values_match", (await service.VerifyGraphicsImportAsync(plan.Id, new())).Status);
    }

    [Fact]
    public async Task MissingReexportAndMissingLanguageRemainPending()
    {
        var p = await Changed();
        Assert.Equal("invalid_context", (await Assert.ThrowsAsync<OperationException>(() => service.PrepareGraphicsImportAsync(p.Id, new(p.CurrentSha256, "App", "Main", "")))).Code);
        var plan = await service.PrepareGraphicsImportAsync(p.Id, new(p.CurrentSha256, "App", "Main", "en-US"));
        Assert.Equal("not_found", (await Assert.ThrowsAsync<OperationException>(() => service.VerifyGraphicsImportAsync(plan.Id, new()))).Code);
    }

    [Fact]
    public async Task ImportPlanRejectsChangedPreparedXmlAndBatchTargets()
    {
        var p = await Changed(); var plan = await service.PrepareGraphicsImportAsync(p.Id, new(p.CurrentSha256, "App", "Main", "en-US"));
        var original = File.ReadAllBytes(plan.XmlPath);
        File.AppendAllText(plan.XmlPath, " ");
        Assert.Equal("artifact_tampered", Assert.Throws<OperationException>(() => service.GraphicsPlan(plan.Id)).Code);
        File.WriteAllBytes(plan.XmlPath, original);
        var batch = XDocument.Load(plan.BatchPath); batch.Root!.Element("import")!.SetAttributeValue("display", "*"); batch.Save(plan.BatchPath);
        Assert.Equal("artifact_tampered", Assert.Throws<OperationException>(() => service.GraphicsPlan(plan.Id)).Code);
    }

    [Fact]
    public async Task ExistingLogixManifestWithoutKindRemainsCompatible()
    {
        var file = Path.Combine(root, "old.l5x"); File.WriteAllText(file, "<RSLogix5000Content><Controller Name='Old'/></RSLogix5000Content>");
        var p = await service.RegisterAsync(file); var meta = Path.Combine(options.StateDirectory, "projects", p.Id, "project.json");
        var json = JsonSerializer.SerializeToNode(p, ProjectService.Json)!.AsObject(); json.Remove("kind"); File.WriteAllText(meta, json.ToJsonString());
        Assert.Equal("logix", service.Get(p.Id).Kind);
    }

    private async Task<ProjectInfo> Changed()
    {
        var p = await service.RegisterAsync(Source());
        var draft = await service.PreviewAsync(p.Id, new(p.CurrentSha256, [new("set_attribute", ObjectPath, "Updated", "caption")]));
        return await service.ApplyAsync(p.Id, draft.Id, new(p.CurrentSha256));
    }
    public void Dispose() => service.Dispose();
}
