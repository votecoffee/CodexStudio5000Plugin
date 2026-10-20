using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;

internal static class DiagnosticWorkflowsIntegration
{
    internal static async Task<List<string>> RunAsync(string root, ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        var catalog = await client.GetAsync<ExtensionCatalog>("/api/extensions");
        async Task<CallToolResult> Call(string operation, object? request = null, string? module = null, string effect = "read", params (string Key, string Value)[] ids)
        {
            var owner = catalog.Extensions.Single(e => e.Id == (module ?? "logix"));
            var args = new Dictionary<string, object?>(); if (request is not null) args["request"] = request; foreach (var id in ids) args[id.Key] = id.Value;
            return await mcp.CallToolAsync("studio_extensions_" + effect, new Dictionary<string, object?> { ["call"] = new ExtensionCall(owner.Id, operation, owner.Generation, JsonSerializer.SerializeToElement(args, ProjectService.Json)) });
        }
        async Task<T> Tool<T>(string operation, object? request = null, string? module = null, string effect = "read", params (string Key, string Value)[] ids)
        {
            var result = await Call(operation, request, module, effect, ids); if (result.IsError == true) throw new Exception(result.Content.OfType<TextContentBlock>().First().Text);
            return JsonSerializer.Deserialize<T>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
        }
        async Task<ProjectInfo> Project(string name, int value, bool invert)
        {
            var doc = XDocument.Load(Path.Combine(root, "examples", "WatchDemo.L5X")); doc.Root!.SetAttributeValue("SoftwareRevision", "35.00"); doc.Root.Element("Controller")!.SetAttributeValue("MajorRev", 35);
            var program = doc.Descendants("Program").Single(); var tags = program.Element("Tags")!;
            LogixStructures.Tag(doc, "Enabled", "WatchProgram").Descendants("DataValue").Single().SetAttributeValue("Value", "1");
            LogixStructures.Tag(doc, "Record", "WatchProgram").Descendants("StructureMember").Single(e => (string?)e.Attribute("Name") == "Inner").Elements("DataValueMember").Single().SetAttributeValue("Value", value);
            tags.Add(XElement.Parse("<Tag Name='EnableAlias' TagType='Alias' DataType='BOOL' AliasFor='Enabled'/>"));
            tags.Add(XElement.Parse("<Tag Name='DiagnosticOutput' TagType='Base' DataType='BOOL'><Data Format='Decorated'><DataValue DataType='BOOL' Value='0'/></Data></Tag>"));
            program.Element("Routines")?.Remove(); program.Add(XElement.Parse("<Routines><Routine Name='main' Type='RLL'><RLLContent><Rung Number='0'><Text>" + (invert ? "XIO" : "XIC") + "(EnableAlias)OTE(DiagnosticOutput);</Text></Rung></RLLContent></Routine></Routines>"));
            var path = Path.Combine(root, "examples", name + ".l5x"); doc.Save(path); return await client.PostAsync<ProjectInfo>("/api/projects", new RegisterRequest(path));
        }
        var a = await Project("DiagnosticsBaseline", 8, false); var b = await Project("DiagnosticsLogic", 8, true); var c = await Project("DiagnosticsFresh", 42, false);
        var traceRequest = new LogicTraceRequest(a.CurrentSha256, "Enabled", "WatchProgram");
        var trace = await Tool<LogicTraceReport>("studio_logic_trace", traceRequest, ids: [("projectId", a.Id)]);
        var restTrace = await client.PostAsync<LogicTraceReport>($"/api/projects/{a.Id}/logic/trace", traceRequest);
        Check(trace.ProjectSha256 == a.CurrentSha256 && trace.Dependencies.Any(e => e.To == "Program:WatchProgram.DiagnosticOutput") && JsonSerializer.Serialize(trace) == JsonSerializer.Serialize(restTrace), "REST and MCP share alias-aware readers/writers and exact saved dependencies");
        var captureRequest = new DiagnosticCaptureRequest(a.Id, a.CurrentSha256, [new("enable", "Enabled", "WatchProgram")]);
        Check((await Call("studio_diagnostic_capture", captureRequest)).IsError == true, "Read-only MCP dispatch rejects capture artifact creation");
        var capture = await Tool<DiagnosticCaptureInfo>("studio_diagnostic_capture", captureRequest, effect: "apply");
        var evaluationRequest = new RungEvaluationRequest(capture.Id, capture.Artifact.Sha256, "/RSLogix5000Content/Controller/Programs/Program[@Name='WatchProgram']/Routines/Routine[@Name='main']", 0);
        var evaluated = await Tool<RungEvaluationReport>("studio_rung_evaluate", evaluationRequest);
        var restEvaluation = await client.PostAsync<RungEvaluationReport>("/api/diagnostics/evaluate", evaluationRequest);
        Check(evaluated.Outputs.Single().Power == "true" && JsonSerializer.Serialize(evaluated) == JsonSerializer.Serialize(restEvaluation), "Captured rung explanation agrees across REST/MCP and follows an alias without reading a controller");
        Check((await Call("studio_rung_evaluate", evaluationRequest with { ExpectedCaptureSha256 = "wrong" })).IsError == true && (await Tool<DiagnosticCaptureInfo[]>("studio_diagnostic_captures")).Any(s => s.Id == capture.Id), "Capture discovery retains the immutable identity and rejects an unrelated hash");
        var w = await client.PostAsync<WatchStatus>("/api/tags/watch", new BrowserWatchRequest(a.Id, a.CurrentSha256, "Diagnostic integration watch", [new("count", "Record", "WatchProgram", "Inner.Count")]));
        var triggerRequest = new DiagnosticTriggerSaveRequest("Generated change window", a.Id, a.CurrentSha256, w.Definition.Id, 0, new("change", "count"), 2, 2);
        var trigger = await Tool<DiagnosticTriggerStatus>("studio_diagnostic_trigger_save", triggerRequest, "watches", "apply");
        var armed = await Tool<DiagnosticTriggerStatus>("studio_diagnostic_trigger_arm", new DiagnosticTriggerArmRequest(0), "watches", "apply", ("triggerId", trigger.Definition.Id));
        Check(armed.State == "armed" && (await client.GetAsync<WatchStatus>("/api/watches/" + w.Definition.Id)).State == "stopped", "Arming a diagnostic window leaves its reader stopped with no native job or controller connection");
        Check((await Call("studio_diagnostic_trigger_save", triggerRequest with { Id = trigger.Definition.Id, ExpectedRevision = 0 }, "watches", "apply")).IsError == true, "An armed diagnostic trigger cannot be edited");
        await Tool<DiagnosticTriggerStatus>("studio_diagnostic_trigger_arm", new DiagnosticTriggerArmRequest(0, false), "watches", "apply", ("triggerId", trigger.Definition.Id));
        var updated = await client.PostAsync<DiagnosticTriggerStatus>("/api/diagnostics/triggers", triggerRequest with { Id = trigger.Definition.Id, ExpectedRevision = 0, Name = "Modified change window" });
        Check(updated.Definition.Revision == 1 && (await Tool<DiagnosticTriggerStatus[]>("studio_diagnostic_triggers", module: "watches")).Any(t => t.Definition.Id == updated.Definition.Id), "Trigger updates use the shared revision and list through MCP");
        var savedTrend = await Tool<SavedTrend>("studio_trend_view_save", new SavedTrendRequest("Generated trend", w.Definition.Id, 0, ["count"]), "watches", "apply");
        savedTrend = await client.PostAsync<SavedTrend>("/api/trends", new SavedTrendRequest("Modified trend", w.Definition.Id, 0, ["count"], Id: savedTrend.Id, ExpectedRevision: 0));
        Check((await Tool<SavedTrend>("studio_trend_view_get", module: "watches", ids: [("trendId", savedTrend.Id)])).Revision == 1 && (await Tool<SavedTrendSummary[]>("studio_trend_views", module: "watches")).Any(t => t.Id == savedTrend.Id), "REST updates and MCP reads a saved trend without changing its watch revision");
        var analysis = await Tool<TrendAnalysis>("studio_trend_analyze", new TrendAnalyzeRequest(savedTrend.Id, 1), "watches");
        Check(!analysis.HasMore && analysis.Statistics.Length == 0 && analysis.Trend.Gaps.Length > 0, "Analyzing an unstarted watch reports an empty stale recording without invented samples");
        var export = new TrendExportRequest(new(savedTrend.Id, 1, analysis.From, analysis.To), analysis.ResultSha256);
        Check((await Call("studio_trend_export", export, "watches")).IsError == true, "Read-only dispatch rejects trend result artifact creation");
        var trendArtifact = await Tool<Artifact>("studio_trend_export", export, "watches", "apply");
        Check(ProjectService.Hash(await client.DownloadAsync(trendArtifact.Id)) == trendArtifact.Sha256 && (await Call("studio_trend_export", export with { ExpectedResultSha256 = "wrong" }, "watches", "apply")).IsError == true, "Trend export retains exact analyzed result bytes and rejects an unrelated result hash");
        var merge = new LogicValueMergeRequest(new(a.Id, a.CurrentSha256), new(b.Id, b.CurrentSha256), new(c.Id, c.CurrentSha256), [new("Record", "WatchProgram", "Record", "WatchProgram")]);
        var preview = await Tool<LogicValueMergeReport>("studio_logic_value_merge_preview", merge);
        Check(preview.CanApply && preview.CopiedValues == 1, "A common baseline separates modified logic from fresh saved UDT values");
        Check((await Call("studio_logic_value_merge_apply", new LogicValueMergeApplyRequest(merge, preview.ReviewSha256))).IsError == true, "Read-only MCP dispatch cannot apply a three-source merge");
        var merged = await Tool<ProjectInfo>("studio_logic_value_merge_apply", new LogicValueMergeApplyRequest(merge, preview.ReviewSha256), effect: "apply");
        var extracted = await client.PostAsync<StructureExtraction>("/api/structures/extract", new StructureExtractRequest(ProjectId: merged.Id, TagName: "Record", ProgramName: "WatchProgram"));
        Check(extracted.Tags.Single().Value!["Inner"]!["Count"]!.GetValue<long>() == 42 && merged.Id != b.Id && (await client.GetAsync<ProjectInfo>("/api/projects/" + b.Id)).Revision == 0, "Reviewed merge creates a separate project with fresh values while preserving modified-logic input");
        var preservation = merge with { Mode = "preserve-fresh", Tags = null };
        var preservedPreview = await Tool<LogicValueMergeReport>("studio_logic_value_merge_preview", preservation);
        Check(preservedPreview.CanApply && preservedPreview.Preservation!.TagRecords > 1 && preservedPreview.PolicySha256 is not null, "Whole-project keep-current-values mode is discoverable with a concise preservation receipt and policy hash");
        var preservedProject = await Tool<ProjectInfo>("studio_logic_value_merge_apply", new LogicValueMergeApplyRequest(preservation, preservedPreview.ReviewSha256), effect: "apply");
        var validationRequest = new LogixValidationRequest("merge-preservation", new(preservedProject.Id, preservedProject.CurrentSha256), Merge: preservation, ExpectedPolicySha256: preservedPreview.PolicySha256);
        var profiles = await Tool<JsonElement>("studio_validation_profiles");
        Check(profiles.GetProperty("profiles").GetArrayLength() == 4, "MCP discovers named offline integrity, merge preservation and existing-readback profiles");
        var validation = await Tool<LogixValidationReport>("studio_validation_preview", validationRequest);
        var restValidation = await client.PostAsync<LogixValidationReport>("/api/logix-validation/preview", validationRequest);
        Check(validation.Status == "pass" && validation.ReviewSha256 == restValidation.ReviewSha256 && validation.UnsupportedCoverage.Length > 0, "REST/MCP share exact policy-bound preservation findings and explicit native/safety/runtime exclusions");
        Check((await Call("studio_validation_record", new LogixValidationRecordRequest(validationRequest, validation.ReviewSha256))).IsError == true, "Read-only MCP dispatch cannot create a retained validation receipt");
        var receipt = await Tool<LogixValidationReceipt>("studio_validation_record", new LogixValidationRecordRequest(validationRequest, validation.ReviewSha256), effect: "apply");
        var retained = await Tool<LogixValidationDocument>("studio_validation_receipt", ids: [("receiptId", receipt.Artifact.Id), ("expectedSha256", receipt.Artifact.Sha256)]);
        Check(retained.Report.ReviewSha256 == validation.ReviewSha256 && ProjectService.Hash(await client.DownloadAsync(receipt.Artifact.Id)) == receipt.Artifact.Sha256, "Complete machine-readable validation receipt remains hash-bound through MCP and artifact download");
        Check((await Call("studio_validation_preview", validationRequest with { ExpectedPolicySha256 = "unreviewed" })).IsError == true, "Validation rejects an unrelated expected-change policy hash");
        // Synthetic completed-export provenance is allowed only inside the owned
        // offline fixture. Never insert these records into a normal app workspace.
        if (Directory.GetParent(Path.GetFullPath(root))?.Name == "isolated-validation")
        {
            var state = Path.Combine(root, ".local", "state"); var artifactId = Guid.NewGuid().ToString("N"); var jobId = Guid.NewGuid().ToString("N");
            var sourceName = (string)XDocument.Load(a.SourcePath).Root!.Element("Controller")!.Attribute("Name")!;
            var l5k = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes("(* Generated fixture μ; no native acceptance *)\r\nIE_VER := 2.26;\r\nCONTROLLER " + sourceName + " (Major := 35, AutoDiagsEnabled := 1)\r\nMODULE Local (Major := 35)\r\nEND_MODULE\r\nEND_CONTROLLER\r\n")).ToArray();
            var artifact = new Artifact(artifactId, a.Id, "l5k", ProjectService.Hash(l5k), l5k.Length, "Generated offline integration fixture");
            var artifactDir = Path.Combine(state, "artifacts", artifactId); Directory.CreateDirectory(artifactDir); File.WriteAllBytes(Path.Combine(artifactDir, "project.l5k"), l5k); File.WriteAllBytes(Path.Combine(artifactDir, "artifact.json"), JsonSerializer.SerializeToUtf8Bytes(artifact, ProjectService.Json));
            var job = new SdkJob(jobId, "succeeded", DateTimeOffset.UnixEpoch, ["SaveAsAsync"], a.CurrentSha256, null, false, [], [], [artifact], a);
            var jobDir = Path.Combine(state, "sdk-jobs", jobId); Directory.CreateDirectory(jobDir); File.WriteAllBytes(Path.Combine(jobDir, "job.json"), JsonSerializer.SerializeToUtf8Bytes(job, ProjectService.Json));
            var legacyRequest = new LegacyReviewRequest(jobId, artifactId, artifact.Sha256, "Generated review-only integration");
            var legacyPlan = await Tool<LegacyReviewPlan>("studio_legacy_review_preview", legacyRequest); var restPlan = await client.PostAsync<LegacyReviewPlan>("/api/legacy-review/preview", legacyRequest);
            Check(legacyPlan.ReviewOnly && legacyPlan.ReviewSha256 == restPlan.ReviewSha256 && legacyPlan.Edits.Length == 4, "REST/MCP share exact review-only legacy fields and native-export provenance without starting jobs");
            var prepare = new LegacyReviewPrepareRequest(legacyRequest, legacyPlan.ReviewSha256);
            Check((await Call("studio_legacy_review_prepare", prepare)).IsError == true, "Read-only dispatch cannot prepare legacy input artifacts");
            var prepared = await Tool<LegacyReviewPrepared>("studio_legacy_review_prepare", prepare, effect: "apply"); var preparedBytes = await client.DownloadAsync(prepared.Input.Id);
            Check(preparedBytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) && ProjectService.Hash(preparedBytes) == legacyPlan.PreparedSha256 && (await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).Length == 1, "Prepared legacy input retains its UTF-8 BOM and creates no import/controller job");
            var manifest = JsonSerializer.Deserialize<LegacyReviewManifest>(await client.DownloadAsync(prepared.Manifest.Id), ProjectService.Json)!;
            Check(manifest.Plan.ReviewOnly && manifest.Plan.NativeExport.Sha256 == artifact.Sha256 && prepared.ImportRequest.SourceArtifactId == prepared.Input.Id && prepared.ImportRequest.Calls.Single().Method == "SaveAsAsync", "Retained review manifest and explicit unexecuted SDK request pin exact input/source hashes");
            Check((await Call("studio_legacy_review_prepare", prepare with { ExpectedReviewSha256 = "stale" }, effect: "apply")).IsError == true, "Legacy preparation rejects stale reviewed fields without starting native work");
            var legacyValidation = new LogixValidationRequest("legacy-review-readback", new(a.Id, a.CurrentSha256), Reference: new(a.Id, a.CurrentSha256));
            var legacyResult = await Tool<LogixValidationReport>("studio_validation_preview", legacyValidation); var legacyRest = await client.PostAsync<LogixValidationReport>("/api/logix-validation/preview", legacyValidation);
            Check(legacyResult.ReviewSha256 == legacyRest.ReviewSha256 && legacyResult.Checks.Any(c => c.Name == "target-version" && c.Status == "fail") && legacyResult.Boundary.StartsWith("REVIEW ONLY"), "REST/MCP legacy readback reports a wrong native target revision rather than accepting source headers");
            var legacyReceipt = await Tool<LogixValidationReceipt>("studio_validation_record", new LogixValidationRecordRequest(legacyValidation, legacyResult.ReviewSha256), effect: "apply");
            Check(legacyReceipt.Status == "fail" && ProjectService.Hash(await client.DownloadAsync(legacyReceipt.Artifact.Id)) == legacyReceipt.Artifact.Sha256, "Review-only failed readback findings remain retained and hash-bound through MCP");
        }
        var jobsBefore = (await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).Length;
        var plan = await Tool<VersionCompatibilityPlan>("studio_version_compatibility_preview", new VersionCompatibilityRequest(a.Id, a.CurrentSha256, 38));
        var downgrade = await Tool<VersionCompatibilityPlan>("studio_version_compatibility_preview", plan.Request with { TargetRevision = 34 });
        Check(plan.CanConvert && !plan.NativeAvailable && plan.NativeRequest.Calls[0].Method == "ConvertAsync" && !downgrade.CanConvert, "Version workflow plans native upgrades and explicitly blocks generic downgrade");
        Check((await Call("studio_version_compatibility_start", new VersionCompatibilityStartRequest(plan.Request, plan.ReviewSha256))).IsError == true &&
            (await Call("studio_version_compatibility_start", new VersionCompatibilityStartRequest(plan.Request, plan.ReviewSha256), effect: "control")).IsError == true && (await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).Length == jobsBefore,
            "Native compatibility start requires control dispatch and unavailable native conversion creates no job");
        await Tool<JsonElement>("studio_diagnostic_trigger_delete", new ReviewDeleteRequest(1), "watches", "apply", ("triggerId", trigger.Definition.Id));
        await Tool<JsonElement>("studio_trend_view_delete", new ReviewDeleteRequest(1), "watches", "apply", ("trendId", savedTrend.Id));
        Check((await client.GetAsync<WatchStatus>("/api/watches/" + w.Definition.Id)).State == "stopped" && new[] { a, b, c }.All(p => ProjectService.Hash(File.ReadAllBytes(p.SourcePath)) == p.SourceSha256), "Removing diagnostic definitions preserves recordings, stopped polling and all source projects");
        return checks;
    }
}
