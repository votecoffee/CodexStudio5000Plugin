using System.IO.Compression;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class HmiLanguageIntegration
{
    internal static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        var catalog = await client.GetAsync<ExtensionCatalog>("/api/extensions");
        foreach (var platform in new[] { "ftview-me", "ftview-se", "view-designer" })
        {
            var vd = platform == "view-designer"; var prefix = vd ? "viewdesigner" : "ftview"; var moduleId = vd ? "viewdesigner" : "factorytalk";
            var module = catalog.Extensions.Single(e => e.Id == moduleId); var source = Path.Combine(root, "examples", vd ? "ViewDesignerV9LanguagesDemo.xlsx" : "FactoryTalkLanguagesDemo.xls");
            var p = await client.PostAsync<HmiLanguageInfo>("/api/hmi-languages", new HmiLanguageRegisterRequest(source, platform));
            async Task<CallToolResult> Call(string verb, object request, bool write = false) => await mcp.CallToolAsync(write ? "studio_extensions_apply" : "studio_extensions_read", new Dictionary<string, object?>
            { ["call"] = new ExtensionCall(moduleId, prefix + "_languages_" + verb, module.Generation, JsonSerializer.SerializeToElement(new { workspaceId = p.Id, request }, ProjectService.Json)) });
            async Task<T> Tool<T>(string verb, object request, bool write = false)
            { var result = await Call(verb, request, write); if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text); return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!; }
            var before = await Tool<HmiLanguagePage>("browse", new HmiLanguageQuery(p.CurrentSha256));
            var rest = await client.PostAsync<HmiLanguagePage>($"/api/hmi-languages/{p.Id}/browse", new HmiLanguageQuery(p.CurrentSha256));
            Check(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(rest), platform + ": REST/MCP return identical native keys, translations and evidence boundaries");
            var locale = vd ? "es-ES" : "zh-CN"; var key = before.Records[0].Key;
            if (vd) Check(before.Records.Length == 3 && before.Records[0].Context[3] == before.Records[1].Context[3] && before.Records[0].Key != before.Records[1].Key && before.Records.All(r => !r.Texts.Single(t => t.Language == "en-US").Writable) &&
                (await Call("preview", new HmiLanguagePreviewRequest(p.CurrentSha256, [new(key, "en-US", "No key edits")]))).IsError == true, "View Designer native v9 metadata/implicit cells retain distinct context identities and block source KEY edits through MCP");
            var request = new HmiLanguagePreviewRequest(p.CurrentSha256, [new(key, locale, "翻译\r\n=literal _x000A_"), new(key, "fr-FR", "Prêt")], ["fr-FR"]);
            var preview = await Tool<HmiLanguagePreview>("preview", request); var httpPreview = await client.PostAsync<HmiLanguagePreview>($"/api/hmi-languages/{p.Id}/preview", request);
            Check(preview.ReviewSha256 == httpPreview.ReviewSha256 && (await client.GetAsync<HmiLanguageInfo>("/api/hmi-languages/" + p.Id)).Revision == 0, platform + ": repeatable previews agree and do not mutate managed state");
            Check((await Call("apply", new HmiLanguageApplyRequest(request, preview.ReviewSha256))).IsError == true && (await Call("apply", new HmiLanguageApplyRequest(request, "wrong"), true)).IsError == true, platform + ": read-only dispatch and altered review hashes cannot apply translations");
            p = await Tool<HmiLanguageInfo>("apply", new HmiLanguageApplyRequest(request, preview.ReviewSha256), true);
            Check((await Call("apply", new HmiLanguageApplyRequest(request, preview.ReviewSha256), true)).IsError == true, platform + ": stale language edits are rejected");
            var after = await Tool<HmiLanguagePage>("browse", new HmiLanguageQuery(p.CurrentSha256, "Prêt"));
            Check(after.Total == 1 && after.Records[0].Key == key && after.Records[0].Texts.Single(t => t.Language == locale).Text == "翻译\r\n=literal _x000A_" && after.Records[0].Texts.Single(t => t.Language == "en-US").Text == before.Records[0].Texts.Single(t => t.Language == "en-US").Text,
                platform + ": Unicode/new-language edits retain native identity and unselected language text");
            Check((await Call("export", new HmiLanguageExportRequest(p.CurrentSha256))).IsError == true, platform + ": artifact creation requires write dispatch");
            var artifact = await Tool<Artifact>("export", new HmiLanguageExportRequest(p.CurrentSha256), true);
            var output = Path.Combine(root, "artifacts", "exports", "hmi-language-" + p.Id + "." + p.Format); await client.PostAsync<SavedArtifact>($"/api/artifacts/{artifact.Id}/save", new SaveArtifactRequest(output));
            var suppliedReexport = Path.Combine(root, "examples", "language-readback-" + p.Id + "." + p.Format); File.Copy(output, suppliedReexport);
            var verify = await Tool<HmiLanguageVerification>("verify", new HmiLanguageVerifyRequest(p.CurrentSha256, suppliedReexport));
            var mismatched = await Tool<HmiLanguageVerification>("verify", new HmiLanguageVerifyRequest(p.CurrentSha256, source));
            Check(verify.Matches && !mismatched.Matches && mismatched.DifferenceCount > 0 && ProjectService.Hash(File.ReadAllBytes(output)) == artifact.Sha256 && ProjectService.Hash(File.ReadAllBytes(source)) == p.SourceSha256,
                platform + ": native-file artifact/export verification reports differences and preserves the original");
        }
        var textSource = Path.Combine(root, "examples", "FactoryTalkLanguagesDemo.txt"); var textWorkspace = await client.PostAsync<HmiLanguageInfo>("/api/hmi-languages", new HmiLanguageRegisterRequest(textSource, "ftview-se", "en-US"));
        Check((await client.PostAsync<HmiLanguagePage>($"/api/hmi-languages/{textWorkspace.Id}/browse", new HmiLanguageQuery(textWorkspace.CurrentSha256))).Total == 2, "FactoryTalk Unicode text uses explicit single-language context with preserved native headers");
        var vdText = await client.PostAsync<HmiLanguageInfo>("/api/hmi-languages", new HmiLanguageRegisterRequest(Path.Combine(root, "examples", "ViewDesignerV9LanguagesDemo.txt"), "view-designer"));
        Check((await client.PostAsync<HmiLanguagePage>($"/api/hmi-languages/{vdText.Id}/browse", new HmiLanguageQuery(vdText.CurrentSha256))).Languages.SequenceEqual(["en-US", "zh-Hans", "es-ES"]), "View Designer Unicode text reads native metadata/context/source-language keys");
        return checks;
    }
    // Optional real-export validation. No running service, native bridge or controller is used.
    internal static async Task ValidateFileAsync(string root, string source, string platform)
    {
        var directory = Path.Combine(root, "artifacts", "hmi-language-validation", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var originalHash = ProjectService.Hash(File.ReadAllBytes(source)); var copy = Path.Combine(directory, "source" + Path.GetExtension(source)); File.Copy(source, copy);
        using var service = new ProjectService(new ServiceOptions { StateDirectory = Path.Combine(directory, "state"), AllowedRoots = [directory], OutputRoots = [directory] });
        var p = await service.RegisterHmiLanguageAsync(new(copy, platform)); var page = service.BrowseHmiLanguage(p.Id, new(p.CurrentSha256));
        var record = page.Records.First(r => r.Texts.Any(t => t.Writable)); var target = record.Texts.Last(t => t.Writable);
        var request = new HmiLanguagePreviewRequest(p.CurrentSha256, [new(record.Key, target.Language, target.Text + "\n[offline validation]")]); var preview = await service.PreviewHmiLanguageAsync(p.Id, request);
        p = await service.ApplyHmiLanguageAsync(p.Id, new(request, preview.ReviewSha256)); var artifact = await service.ExportHmiLanguageAsync(p.Id, new(p.CurrentSha256));
        var output = Path.Combine(directory, "translated." + p.Format); await service.SaveArtifactAsync(artifact.Id, output);
        var exact = service.VerifyHmiLanguage(p.Id, new(p.CurrentSha256, output)); var delta = service.VerifyHmiLanguage(p.Id, new(p.CurrentSha256, copy));
        var unchangedParts = true; var preserved = 0;
        if (File.ReadAllBytes(copy).AsSpan().StartsWith(new byte[] { 0x50, 0x4b }))
        {
            using var a = ZipFile.OpenRead(copy); using var b = ZipFile.OpenRead(output);
            foreach (var part in a.Entries.Where(e => !e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal)))
            {
                using var aa = part.Open(); using var bb = b.GetEntry(part.FullName)!.Open(); using var av = new MemoryStream(); using var bv = new MemoryStream(); aa.CopyTo(av); bb.CopyTo(bv);
                unchangedParts &= av.ToArray().SequenceEqual(bv.ToArray()); preserved++;
            }
        }
        var preservedSource = originalHash == ProjectService.Hash(File.ReadAllBytes(source));
        if (!exact.Matches || delta.DifferenceCount != 1 || !unchangedParts || !preservedSource) throw new Exception("Real-export offline validation failed.");
        var report = new { platform, records = page.Total, languages = page.Languages, originalSha256 = originalHash, resultSha256 = artifact.Sha256, preservedSource, unchangedPackageParts = preserved, exact.Matches,
            changedTranslations = delta.DifferenceCount, controllerConnectionAttempted = false, nativeStudioImportObserved = false };
        File.WriteAllText(Path.Combine(directory, "validation.json"), JsonSerializer.Serialize(report, ProjectService.Json)); Console.WriteLine(JsonSerializer.Serialize(report, ProjectService.Json)); Console.WriteLine("Private validation output: " + directory);
    }
}
