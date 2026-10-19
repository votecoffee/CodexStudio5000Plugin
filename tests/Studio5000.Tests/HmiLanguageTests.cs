using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class HmiLanguageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hmi-languages-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public HmiLanguageTests()
    { Directory.CreateDirectory(root); service = new(new ServiceOptions { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root], OutputRoots = [root] }); }
    private string Example(string name)
    { var target = Path.Combine(root, name); File.Copy(Path.Combine(AppContext.BaseDirectory, name), target, true); return target; }
    private HmiLanguagePage Browse(HmiLanguageInfo p) => service.BrowseHmiLanguage(p.Id, new(p.CurrentSha256));
    private async Task<HmiLanguageInfo> Apply(HmiLanguageInfo p, HmiLanguagePreviewRequest request)
    { var preview = await service.PreviewHmiLanguageAsync(p.Id, request); return await service.ApplyHmiLanguageAsync(p.Id, new(request, preview.ReviewSha256)); }
    [Theory]
    [InlineData("ftview-me")]
    [InlineData("ftview-se")]
    public async Task OpenXmlXlsPreservesSourceKeysOtherTranslationsAndUnmodifiedPackageParts(string platform)
    {
        var path = Example("FactoryTalkLanguagesDemo.xls"); var original = File.ReadAllBytes(path);
        var p = await service.RegisterHmiLanguageAsync(new(path, platform)); var before = Browse(p);
        Assert.Equal(3, before.Total); Assert.Equal(["en-US", "zh-CN"], before.Languages);
        Assert.Contains(before.Records, r => r.Texts.Any(t => t.Text == "Line 1\r\nLine 2 _x1234_"));
        p = await Apply(p, new(p.CurrentSha256, [new(before.Records[0].Key, "zh-CN", "已准备\r\n_x000A_ =literal +中文")], ["fr-FR"]));
        var after = Browse(p); Assert.Equal("已准备\r\n_x000A_ =literal +中文", after.Records[0].Texts.Single(t => t.Language == "zh-CN").Text);
        Assert.All(after.Records, r => Assert.Equal("", r.Texts.Single(t => t.Language == "fr-FR").Text));
        Assert.Equal(before.Records.Select(r => r.Key), after.Records.Select(r => r.Key));
        for (var i = 0; i < before.Records.Length; i++) Assert.Equal(before.Records[i].Texts.Single(t => t.Language == "en-US"), after.Records[i].Texts.Single(t => t.Language == "en-US"));
        var artifact = await service.ExportHmiLanguageAsync(p.Id, new(p.CurrentSha256)); var output = Path.Combine(root, "translated.xls"); await service.SaveArtifactAsync(artifact.Id, output);
        Assert.True(service.VerifyHmiLanguage(p.Id, new(p.CurrentSha256, output)).Matches);
        Assert.False(service.VerifyHmiLanguage(p.Id, new(p.CurrentSha256, path)).Matches);
        using var oldZip = new ZipArchive(new MemoryStream(original)); using var newZip = ZipFile.OpenRead(output);
        foreach (var entry in oldZip.Entries.Where(e => e.FullName != "xl/worksheets/sheet1.xml"))
        { using var a = entry.Open(); using var b = newZip.GetEntry(entry.FullName)!.Open(); using var aa = new MemoryStream(); using var bb = new MemoryStream(); a.CopyTo(aa); b.CopyTo(bb); Assert.Equal(aa.ToArray(), bb.ToArray()); }
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Contains(service.History(p.Id), e => e.Action == "save_artifact");
    }
    [Theory]
    [InlineData("ViewDesignerLanguagesDemo.xlsx")]
    [InlineData("ViewDesignerLanguagesDemo.txt")]
    public async Task ViewDesignerKeysAreImmutableAndNewLanguageColumnsPreserveExistingOnes(string name)
    {
        var path = Example(name); var p = await service.RegisterHmiLanguageAsync(new(path, "view-designer")); var before = Browse(p);
        p = await Apply(p, new(p.CurrentSha256, [new(before.Records[0].Key, "es-ES", "Preparado\t\"好\"\nListo"), new(before.Records[1].Key, "ja-JP", "開始 {#1}")], ["ja-JP"]));
        var after = Browse(p); Assert.Equal(before.Records.Select(r => r.Context), after.Records.Select(r => r.Context));
        Assert.Equal("Preparado\t\"好\"\nListo", after.Records[0].Texts.Single(t => t.Language == "es-ES").Text); Assert.Equal("開始 {#1}", after.Records[1].Texts.Single(t => t.Language == "ja-JP").Text);
        Assert.All(after.Records, r => Assert.Contains(r.Texts, t => t.Language == "en-US"));
    }
    [Theory]
    [InlineData("ftview-me")]
    [InlineData("ftview-se")]
    public async Task SingleLanguageUnicodeTextPreservesNativeHeaderAndEscapeSyntax(string platform)
    {
        var path = Example("FactoryTalkLanguagesDemo.txt"); var original = File.ReadAllBytes(path); var p = await service.RegisterHmiLanguageAsync(new(path, platform, "zh-CN"));
        var row = Browse(p).Records[0]; p = await Apply(p, new(p.CurrentSha256, [new(row.Key, "zh-CN", "就绪\\n启动\t\"提示\"")]));
        var artifact = await service.ExportHmiLanguageAsync(p.Id, new(p.CurrentSha256)); var bytes = File.ReadAllBytes(service.Download(artifact.Id).Path);
        Assert.Equal(original[..(2 + Encoding.Unicode.GetString(original[2..]).IndexOf("Graphic Display", StringComparison.Ordinal) * 2)], bytes[..(2 + Encoding.Unicode.GetString(bytes[2..]).IndexOf("Graphic Display", StringComparison.Ordinal) * 2)]);
        Assert.Equal("就绪\\n启动\t\"提示\"", Browse(p).Records[0].Texts[0].Text);
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewHmiLanguageAsync(p.Id, new(p.CurrentSha256, [], ["de-DE"])));
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(path, platform)));
    }
    [Fact]
    public async Task ExactReviewAndSourceHashesBlockStaleOrChangedBatchesAtomically()
    {
        var p = await service.RegisterHmiLanguageAsync(new(Example("FactoryTalkLanguagesDemo.xls"), "ftview-me")); var key = Browse(p).Records[0].Key;
        var request = new HmiLanguagePreviewRequest(p.CurrentSha256, [new(key, "zh-CN", "Changed")]); var preview = await service.PreviewHmiLanguageAsync(p.Id, request);
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyHmiLanguageAsync(p.Id, new(request with { Edits = [new(key, "zh-CN", "Changed differently")] }, preview.ReviewSha256)));
        var next = await service.ApplyHmiLanguageAsync(p.Id, new(request, preview.ReviewSha256));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyHmiLanguageAsync(p.Id, new(request, preview.ReviewSha256))); Assert.Equal(1, next.Revision);
        foreach (var bad in new[] { new HmiLanguagePreviewRequest(next.CurrentSha256, [new(key, "en-US", "Fine"), new("missing-key", "zh-CN", "Bad")]), new(next.CurrentSha256, [new(key, "en-US", "A"), new(key, "EN-us", "B")]), new(next.CurrentSha256, [new(key, "KEY", "No")]), new(next.CurrentSha256, [new(key, "en-US", "bad\u0001")]) })
            await Assert.ThrowsAsync<OperationException>(() => service.PreviewHmiLanguageAsync(p.Id, bad));
        Assert.Equal(next.CurrentSha256, service.HmiLanguage(p.Id).CurrentSha256);
    }
    private static void Mutate(string path, string part, Action<XDocument> action)
    { using var zip = ZipFile.Open(path, ZipArchiveMode.Update); var entry = zip.GetEntry(part)!; XDocument xml; using (var stream = entry.Open()) xml = XDocument.Load(stream); action(xml); entry.Delete(); using var output = zip.CreateEntry(part).Open(); xml.Save(output); }
    [Theory]
    [InlineData("ViewDesignerV9LanguagesDemo.xlsx")]
    [InlineData("ViewDesignerV9LanguagesDemo.txt")]
    public async Task NativeViewDesignerContextKeysSourceLanguageAndMetadataArePreserved(string name)
    {
        var path = Example(name); var original = File.ReadAllBytes(path); var p = await service.RegisterHmiLanguageAsync(new(path, "view-designer")); var before = Browse(p);
        Assert.Equal(3, before.Total); Assert.Equal(3, before.Languages.Length); Assert.Equal("en-US", before.Languages[0]);
        Assert.Equal("Ready", before.Records[0].Context[3]); Assert.Equal("Ready", before.Records[1].Context[3]); Assert.NotEqual(before.Records[0].Key, before.Records[1].Key);
        Assert.Equal("", before.Records[2].Context[3]); Assert.All(before.Records, r => Assert.False(r.Texts.Single(t => t.Language == "en-US").Writable));
        var sourceEdit = new HmiLanguagePreviewRequest(p.CurrentSha256, [new(before.Records[0].Key, "en-US", "Changed key")]);
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewHmiLanguageAsync(p.Id, sourceEdit));
        p = await Apply(p, new(p.CurrentSha256, [new(before.Records[0].Key, "zh-Hans", "已就绪"), new(before.Records[1].Key, "fr-FR", "Prêt"), new(before.Records[2].Key, "fr-FR", "Option")], ["fr-FR"]));
        var after = Browse(p); Assert.Equal(before.Records.Select(r => r.Context), after.Records.Select(r => r.Context));
        Assert.Equal("已就绪", after.Records[0].Texts.Single(t => t.Language == "zh-Hans").Text); Assert.Equal("准备", after.Records[1].Texts.Single(t => t.Language == "zh-Hans").Text);
        Assert.Equal("Option", after.Records[2].Texts.Single(t => t.Language == "fr-FR").Text);
        var artifact = await service.ExportHmiLanguageAsync(p.Id, new(p.CurrentSha256)); var output = Path.Combine(root, "native-translated." + p.Format); await service.SaveArtifactAsync(artifact.Id, output);
        Assert.True(service.VerifyHmiLanguage(p.Id, new(p.CurrentSha256, output)).Matches); Assert.Equal(original, File.ReadAllBytes(path));
        if (p.Format == "xlsx")
        {
            XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var a = ZipFile.OpenRead(path); using var b = ZipFile.OpenRead(output); using var aa = a.GetEntry("xl/worksheets/sheet.xml")!.Open(); using var bb = b.GetEntry("xl/worksheets/sheet.xml")!.Open();
            var oldRows = XDocument.Load(aa).Descendants(s + "row").ToArray(); var newRows = XDocument.Load(bb).Descendants(s + "row").ToArray();
            Assert.Equal(oldRows.Take(7).Select(r => r.ToString()), newRows.Take(7).Select(r => r.ToString())); Assert.Equal(oldRows.Last().ToString(), newRows.Last().ToString());
            Assert.All(newRows, r => Assert.Null(r.Attribute("r")));
            for (var i = 0; i < oldRows.Length; i++) Assert.All(newRows[i].Elements(s + "c").Take(oldRows[i].Elements(s + "c").Count()), c => Assert.Null(c.Attribute("r")));
        }
        else
        {
            var oldText = Encoding.Unicode.GetString(original[2..]); var newText = Encoding.Unicode.GetString(File.ReadAllBytes(output)[2..]);
            Assert.Equal(oldText[..oldText.IndexOf("Container\t", StringComparison.Ordinal)], newText[..newText.IndexOf("Container\t", StringComparison.Ordinal)]); Assert.EndsWith("\r\n\r\n", newText);
        }
    }
    [Fact]
    public async Task SparseCellsAndNewLanguageColumnsDoNotShiftOriginalAddresses()
    {
        var path = Example("ViewDesignerLanguagesDemo.xlsx"); XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Mutate(path, "xl/worksheets/sheet1.xml", doc => doc.Descendants(s + "c").Single(c => (string?)c.Attribute("r") == "B2").Remove());
        var p = await service.RegisterHmiLanguageAsync(new(path, "view-designer")); var row = Browse(p).Records[0]; Assert.Equal("", row.Texts.Single(t => t.Language == "en-US").Text);
        p = await Apply(p, new(p.CurrentSha256, [new(row.Key, "en-US", "Restored"), new(row.Key, "fr-FR", "Prêt")], ["fr-FR"]));
        var after = Browse(p).Records[0]; Assert.Equal("Listo", after.Texts.Single(t => t.Language == "es-ES").Text); Assert.Equal("Restored", after.Texts.Single(t => t.Language == "en-US").Text);
    }
    [Theory]
    [InlineData("row")]
    [InlineData("cell")]
    public async Task AmbiguousOrOutOfOrderExplicitAddressesAreRejected(string shape)
    {
        var path = Example("ViewDesignerV9LanguagesDemo.xlsx"); XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Mutate(path, "xl/worksheets/sheet.xml", doc => { var row = doc.Descendants(s + "row").Skip(8).First(); if (shape == "row") row.SetAttributeValue("r", "1"); else row.Elements(s + "c").Skip(2).First().SetAttributeValue("r", "A9"); });
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(path, "view-designer")));
    }
    [Fact]
    public async Task EmptyFinalTextCellCanBeUpdatedInTheSameBatchAsAddingALanguage()
    {
        var path = Path.Combine(root, "empty.txt"); File.WriteAllText(path, "KEY\ten-US\tes-ES\r\n#Prompt\tReady\t\r\n", new UTF8Encoding(true));
        var p = await service.RegisterHmiLanguageAsync(new(path, "view-designer")); var key = Browse(p).Records[0].Key;
        p = await Apply(p, new(p.CurrentSha256, [new(key, "es-ES", "Listo"), new(key, "fr-FR", "Prêt")], ["fr-FR"]));
        var row = Browse(p).Records[0]; Assert.Equal("#Prompt", row.Context[0]); Assert.Equal("Listo", row.Texts.Single(t => t.Language == "es-ES").Text); Assert.Equal("Prêt", row.Texts.Single(t => t.Language == "fr-FR").Text);
    }
    [Fact]
    public async Task FormulaRichMergedProtectedAndDuplicateCellsCannotBeFlattened()
    {
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        foreach (var shape in new[] { "formula", "rich", "merged", "protected" })
        {
            var path = Example("FactoryTalkLanguagesDemo.xls");
            Mutate(path, "xl/worksheets/sheet1.xml", doc =>
            {
                var cell = doc.Descendants(s + "c").Single(c => (string?)c.Attribute("r") == "F2");
                if (shape == "formula") cell.Add(new XElement(s + "f", "1+1"));
                if (shape == "rich") { cell.SetAttributeValue("t", "inlineStr"); cell.ReplaceNodes(new XElement(s + "is", new XElement(s + "r", new XElement(s + "t", "Rich")))); }
                if (shape == "merged") doc.Root!.Add(new XElement(s + "mergeCells", new XElement(s + "mergeCell", new XAttribute("ref", "F2:F3"))));
                if (shape == "protected") doc.Root!.Add(new XElement(s + "sheetProtection", new XAttribute("sheet", "1")));
            });
            var p = await service.RegisterHmiLanguageAsync(new(path, "ftview-me")); var row = Browse(p).Records[0]; Assert.False(row.Texts.Single(t => t.Language == "zh-CN").Writable);
            await Assert.ThrowsAsync<OperationException>(() => service.PreviewHmiLanguageAsync(p.Id, new(p.CurrentSha256, [new(row.Key, "zh-CN", "No")])));
        }
        var duplicate = Example("FactoryTalkLanguagesDemo.xls"); Mutate(duplicate, "xl/worksheets/sheet1.xml", doc => doc.Descendants(s + "c").Single(c => (string?)c.Attribute("r") == "F1").Element(s + "v")!.Value = doc.Descendants(s + "c").Single(c => (string?)c.Attribute("r") == "E1").Element(s + "v")!.Value);
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(duplicate, "ftview-me")));
    }
    [Fact]
    public async Task RejectsUnrecognizedBinaryXmlDuplicateKeysAndWrongPlatform()
    {
        var path = Path.Combine(root, "bad.xls"); File.WriteAllBytes(path, [0xd0, 0xcf, 0x11, 0xe0, 1, 2]);
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(path, "ftview-se")));
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(Example("FactoryTalkLanguagesDemo.xls"), "view-designer")));
        var text = Example("ViewDesignerLanguagesDemo.txt"); File.AppendAllText(text, "Ready\tOther\tOtro\r\n", Encoding.Unicode);
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(text, "view-designer")));
        File.WriteAllText(path, "<!DOCTYPE x [<!ENTITY z SYSTEM 'file:///unavailable'>]><x>&z;</x>");
        await Assert.ThrowsAsync<OperationException>(() => service.RegisterHmiLanguageAsync(new(path, "ftview-se")));
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
