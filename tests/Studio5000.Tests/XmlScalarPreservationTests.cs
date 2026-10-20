using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class XmlScalarPreservationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellScalarEdits", Guid.NewGuid().ToString("N"));
    private const string Description = "/RSLogix5000Content/Controller/Tags/Tag[@Name='Example']/Description";
    private const string Tag = "/RSLogix5000Content/Controller/Tags/Tag[@Name='Example']";
    public XmlScalarPreservationTests() => Directory.CreateDirectory(root);
    private static string Source(string encoding = "utf-8") => "<?xml version='1.0' encoding='" + encoding + "'?>\r\n<!-- outside μ 🚀 -->\n<RSLogix5000Content><Controller Name='Demo'>\r\n<Tags><Tag Name='Example' TagType = 'Base' DataType=\"DINT\" ExternalAccess = 'Read/Write'>\r\n<Description><![CDATA[old </Description> μ]]></Description>\n<Data Format='L5K'><![CDATA[35\r\n35]]></Data>\r</Tag></Tags>\r\n</Controller></RSLogix5000Content>\r\n";
    private static byte[] Bytes(string text, Encoding encoding, bool bom) => (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes(text)).ToArray();
    [Theory]
    [InlineData("utf8", false)] [InlineData("utf8", true)] [InlineData("utf16", false)] [InlineData("utf16", true)] [InlineData("utf16be", true)] [InlineData("utf32", true)] [InlineData("utf32be", true)]
    public void Preserves_encoding_BOM_mixed_line_endings_quotes_and_all_unselected_bytes(string name, bool bom)
    {
        Encoding encoding = name switch { "utf16" => new UnicodeEncoding(false, true, true), "utf16be" => new UnicodeEncoding(true, true, true), "utf32" => new UTF32Encoding(false, true, true), "utf32be" => new UTF32Encoding(true, true, true), _ => new UTF8Encoding(true, true) };
        var text = Source(encoding.WebName); var before = Bytes(text, encoding, bom);
        var result = XmlProjects.Apply(before, [new("set_text", Description, "new μ 🚀"), new("set_attribute", Tag, "None", "ExternalAccess")]);
        var expected = text.Replace("<![CDATA[old </Description> μ]]>", "<![CDATA[new μ 🚀]]>").Replace("ExternalAccess = 'Read/Write'", "ExternalAccess = 'None'");
        Assert.Equal(Bytes(expected, encoding, bom), result);
    }
    [Fact]
    public void Repeated_edits_value_dependent_selectors_noops_and_self_closing_nodes_keep_their_exact_scope()
    {
        var text = Source().Replace("<Description><![CDATA[old </Description> μ]]></Description>", "<Description kind = 'before' />"); var original = Bytes(text, new UTF8Encoding(true), true);
        var result = XmlProjects.Apply(original, [new("set_text", Description, "first"), new("set_text", Description + "[text()='first']", "last"), new("set_attribute", Description, "after", "kind")]);
        Assert.Equal(Bytes(text.Replace("<Description kind = 'before' />", "<Description kind = 'after' ><![CDATA[last]]></Description>"), new UTF8Encoding(true), true), result);
        Assert.Equal(original, XmlProjects.Apply(original, [new("set_text", Description, "temporary"), new("set_text", Description, ""), new("set_attribute", Description, "before", "kind")]));
    }
    [Fact]
    public void Escapes_attribute_values_and_splits_CDATA_delimiters_without_creating_markup()
    {
        var value = "quote'\" & < >\r\n\t μ";
        var result = XmlProjects.Apply(Encoding.UTF8.GetBytes(Source()), [new("set_attribute", Tag, value, "ExternalAccess"), new("set_text", Description, "literal ]]> <Fake/> & μ\r\nnext")]);
        var doc = XmlProjects.Parse(result);
        Assert.Equal(value, XmlProjects.Select(doc, Tag).Attribute("ExternalAccess")!.Value);
        Assert.Equal("literal ]]> <Fake/> & μ\nnext", XmlProjects.Select(doc, Description).Value); Assert.Empty(doc.Descendants("Fake"));
        Assert.Contains("ExternalAccess = '", Encoding.UTF8.GetString(result));
    }
    [Fact]
    public void Tracks_namespaced_leaf_names_and_astral_characters_before_attribute_positions()
    {
        var text = Source().Replace("<Description>", "<p:Description xmlns:p='urn:example' marker = '🚀' x='old'>").Replace("</Description>\n", "</p:Description>\n");
        var xpath = "//*[local-name()='Description']";
        var result = XmlProjects.Apply(Encoding.UTF8.GetBytes(text), [new("set_attribute", xpath, "new", "x"), new("set_text", xpath, "changed")]);
        Assert.Equal(text.Replace("x='old'", "x='new'").Replace("<![CDATA[old </Description> μ]]>", "<![CDATA[changed]]>"), Encoding.UTF8.GetString(result));
    }
    [Fact]
    public void Unrepresentable_values_and_invalid_targets_fail_without_a_serializing_fallback()
    {
        var ascii = Source("us-ascii").Replace("μ", "u").Replace("🚀", "rocket");
        Assert.Equal("xml_preservation_encoding", Assert.Throws<OperationException>(() => XmlProjects.Apply(Encoding.ASCII.GetBytes(ascii), [new("set_text", Description, "μ")])).Code);
        Assert.Equal("invalid_attribute", Assert.Throws<OperationException>(() => XmlProjects.Apply(Encoding.UTF8.GetBytes(Source()), [new("set_attribute", Tag, "Renamed", "Name")])).Code);
        Assert.Equal("invalid_target", Assert.Throws<OperationException>(() => XmlProjects.Apply(Encoding.UTF8.GetBytes(Source()), [new("set_text", Tag, "bad")])).Code);
        Assert.ThrowsAny<Exception>(() => XmlProjects.Apply(Encoding.UTF8.GetBytes(Source()), [new("set_text", Description, "\u0001")]));
    }
    [Fact]
    public void Seventy_three_edits_preserve_a_large_opaque_value_and_surrounding_native_bytes()
    {
        var descriptions = string.Concat(Enumerable.Range(0, 73).Select(i => "<Tag Name='Tag" + i + "' TagType='Base' DataType='DINT'><Description><![CDATA[old-" + i + "]]></Description></Tag>\r\n"));
        var padding = string.Concat(Enumerable.Repeat("opaque\r\n", 512000));
        var text = "<?xml version='1.0' encoding='utf-8'?>\r\n<RSLogix5000Content><Controller Name='Demo'><Tags>" + descriptions + "<Tag Name='Pad'><Data><![CDATA[" + padding + "]]></Data></Tag></Tags></Controller></RSLogix5000Content>\r\n";
        var edits = Enumerable.Range(0, 73).Select(i => new Edit("set_text", "/RSLogix5000Content/Controller/Tags/Tag[@Name='Tag" + i + "']/Description", "new-" + i)).ToArray();
        var expected = text; for (var i = 0; i < 73; i++) expected = expected.Replace("<![CDATA[old-" + i + "]]>", "<![CDATA[new-" + i + "]]>");
        Assert.Equal(Bytes(expected, new UTF8Encoding(true), true), XmlProjects.Apply(Bytes(text, new UTF8Encoding(true), true), edits));
    }
    [Fact]
    public async Task Preview_apply_and_legacy_drafts_retain_their_reviewed_byte_contracts()
    {
        var path = Path.Combine(root, "generated.L5X"); var original = Bytes(Source(), new UTF8Encoding(true), true); File.WriteAllBytes(path, original);
        using var service = new ProjectService(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
        var p = await service.RegisterAsync(path); Edit[] edits = [new("set_text", Description, "new")]; var preview = await service.PreviewAsync(p.Id, new(p.CurrentSha256, edits));
        Assert.Equal("scalar-byte-spans-v1", preview.XmlPreservation);
        var applied = await service.ApplyAsync(p.Id, preview.Id, new(p.CurrentSha256)); var artifact = await service.ExportAsync(p.Id, new(applied.CurrentSha256));
        Assert.Equal(Bytes(Source().Replace("<![CDATA[old </Description> μ]]>", "<![CDATA[new]]>"), new UTF8Encoding(true), true), File.ReadAllBytes(service.Download(artifact.Id).Path)); Assert.Equal(original, File.ReadAllBytes(path));
        var oldProject = await service.RegisterAsync(path); var serialized = XmlProjects.Apply(original, edits, preserveScalarBytes: false);
        var oldDraft = new ChangePreview(Guid.NewGuid().ToString("N"), oldProject.Id, oldProject.CurrentSha256, ProjectService.Hash(serialized), edits, "Generated legacy draft", DateTimeOffset.UnixEpoch);
        File.WriteAllBytes(Path.Combine(root, "state", "projects", oldProject.Id, oldDraft.Id + ".draft.json"), JsonSerializer.SerializeToUtf8Bytes(oldDraft, ProjectService.Json));
        var oldApplied = await service.ApplyAsync(oldProject.Id, oldDraft.Id, new(oldProject.CurrentSha256)); Assert.Equal(ProjectService.Hash(serialized), oldApplied.CurrentSha256);
    }
    [Theory]
    [InlineData("ME")] [InlineData("SE")]
    public void FactoryTalk_scalar_edits_share_preservation_and_retain_schema_identity_guards(string edition)
    {
        var schema = "<xs:schema xmlns:xs='http://www.w3.org/2001/XMLSchema'><xs:element name='gfx'><xs:complexType><xs:sequence><xs:element name='label' maxOccurs='unbounded'><xs:complexType><xs:simpleContent><xs:extension base='xs:string'><xs:attribute name='name' type='xs:string'/><xs:attribute name='caption' type='xs:string'/></xs:extension></xs:simpleContent></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element></xs:schema>";
        File.WriteAllText(Path.Combine(root, "Gfx-" + edition + "17.xsd"), schema);
        var text = "<?xml version='1.0' encoding='utf-16'?>\r\n<gfx xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='Gfx-" + edition + "17.xsd'>\r\n<label name='First' caption = 'old'>old &amp; text</label>\r\n<label name='Second' caption='keep'>unchanged</label>\r\n</gfx>\r\n";
        var expected = text.Replace("caption = 'old'", "caption = 'new'").Replace("old &amp; text", "new &lt; &amp; text");
        var result = FactoryTalkGraphics.Apply(Bytes(text, Encoding.Unicode, true), [new("set_attribute", "/gfx/label[@name='First']", "new", "caption"), new("set_text", "/gfx/label[@name='First']", "new < & text")], root);
        Assert.Equal(Bytes(expected, Encoding.Unicode, true), result);
        Assert.Equal("invalid_attribute", Assert.Throws<OperationException>(() => FactoryTalkGraphics.Apply(result, [new("set_attribute", "/gfx/label[@name='First']", "Other", "name")], root)).Code);
    }
    public void Dispose()
    {
        var absolute = Path.GetFullPath(root); var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RockwellScalarEdits")) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected fixture cleanup path."); Directory.Delete(absolute, true);
    }
}
