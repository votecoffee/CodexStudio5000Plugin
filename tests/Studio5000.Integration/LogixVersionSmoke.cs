using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;

internal static class LogixVersionSmoke
{
    public static async Task RunAsync(string root, int revision, string bridgePath)
    {
        if (revision < 37 || !File.Exists(bridgePath)) throw new ArgumentException("This offline smoke includes BuildAsync: select an installed Logix revision >=37 and a built native bridge.");
        var evidence = Path.Combine(root, "artifacts", "test-results", $"logix-v{revision}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        using var service = new ProjectService(new ServiceOptions { StateDirectory = Path.Combine(evidence, "state"),
            AllowedRoots = [evidence], OutputRoots = [evidence], NativeBridgePath = Path.GetFullPath(bridgePath),
            NativeTimeoutSeconds = 240, EnableControllerOperations = false, ControllerTargets = [] });
        var checks = new List<string>(); var jobs = new List<SdkJob>(); var passed = false;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        static SdkCall Call(string method, object? arguments = null) => new(method,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments ?? new { }, ProjectService.Json))!);
        async Task<SdkJob> Job(string name, SdkRunRequest request)
        {
            if (request.ControllerTargetId is not null || SdkCatalog.NeedsController(request.Calls)) throw new InvalidOperationException("Version smoke permits generated offline jobs only.");
            Console.WriteLine($"Starting offline v{revision}: {name}");
            var job = await service.StartSdkJobAsync(request); var timer = Stopwatch.StartNew();
            while (job.Status is "queued" or "running")
            {
                if (timer.Elapsed > TimeSpan.FromMinutes(5))
                {
                    service.CancelSdkJob(job.Id);
                    for (var i = 0; i < 100 && job.Status is "queued" or "running"; i++) { await Task.Delay(100); job = service.SdkJob(job.Id); }
                    jobs.Add(job); throw new TimeoutException("Generated offline SDK job timed out; retained evidence. No automatic retry.");
                }
                await Task.Delay(250); job = service.SdkJob(job.Id);
            }
            jobs.Add(job);
            Check(job.Status == "succeeded" && !job.ControllerEffectsPossible, name + " succeeds without controller effects: " + (job.Error ?? job.Status));
            return job;
        }
        XDocument NativeXml(SdkJob job)
        {
            var artifact = job.Artifacts.Single(a => a.Validation.Contains("output final.l5x;", StringComparison.Ordinal));
            var bytes = File.ReadAllBytes(service.Download(artifact.Id).Path);
            Check(ProjectService.Hash(bytes) == artifact.Sha256, "Native L5X artifact hash matches its recorded bytes");
            var document = XmlProjects.Parse(bytes);
            Check(((string?)document.Root!.Attribute("SoftwareRevision"))?.Split('.')[0] == revision.ToString() &&
                (int?)document.Root.Element("Controller")!.Attribute("MajorRev") == revision, "Native export retains requested software/controller major revision");
            return document;
        }
        static Artifact Acd(SdkJob job) => job.Artifacts.Single(a => a.Validation.Contains("output final.acd;", StringComparison.Ordinal));
        const string program = "VersionFixture";
        static string TagPath(string tag) => $"Controller/Programs/Program[@Name='{program}']/Tags/Tag[@Name='{tag}']";
        try
        {
            var coverage = await service.SdkCoverageAsync();
            Check(coverage.GetProperty("matchesFrozenSurface").GetBoolean() && coverage.GetProperty("allOperationsBound").GetBoolean(), "Current native bridge binds the installed SDK surface");
            var created = await Job("create / build / save", new([
                Call("CreateNewProjectAsync", new { majorRevision = revision, processorTypeName = "1756-L81E", controllerName = "VersionFixture" }),
                Call("BuildAsync"), Call("SaveAsync")]));
            NativeXml(created);
            var project = created.ResultProject ?? throw new Exception("Native output was not adopted as an editable project.");
            var original = Acd(created); var originalHash = ProjectService.Hash(File.ReadAllBytes(service.Download(original.Id).Path));
            var draft = await service.PreviewAsync(project.Id, new(project.CurrentSha256, [new("add_element", "/RSLogix5000Content/Controller/Programs", """
                <Program Name="VersionFixture" MainRoutineName="MainRoutine" Disabled="false" Class="Standard"><Tags>
                <Tag Name="Input" TagType="Base" DataType="DINT" Constant="false" ExternalAccess="Read/Write"/>
                <Tag Name="Precise" TagType="Base" DataType="LREAL" Constant="false" ExternalAccess="Read/Write"/>
                <Tag Name="Unsigned" TagType="Base" DataType="ULINT" Constant="false" ExternalAccess="Read/Write"/>
                </Tags><Routines><Routine Name="MainRoutine" Type="RLL"><RLLContent><Rung Number="0" Type="N"><Text><![CDATA[NOP();]]></Text></Rung></RLLContent></Routine></Routines></Program>
                """)]));
            var edited = await service.ApplyAsync(project.Id, draft.Id, new(project.CurrentSha256));
            var converted = await service.ExportAsync(edited.Id, new(edited.CurrentSha256, "acd"));
            Check(converted.Bytes > 0, $"Managed v{revision} local program/tag edits convert to a new native ACD");
            var typed = await Job("offline typed tag round trips / build", new([
                Call("SetTagValueDINTAsync", new { tagPath = TagPath("Input"), mode = "Offline", tagValue = 41 }),
                Call("GetTagValueDINTAsync", new { tagPath = TagPath("Input"), mode = "Offline" }),
                Call("SetTagValueLREALAsync", new { tagPath = TagPath("Precise"), mode = "Offline", tagValue = 1.25 }),
                Call("GetTagValueLREALAsync", new { tagPath = TagPath("Precise"), mode = "Offline" }),
                Call("SetTagValueULINTAsync", new { tagPath = TagPath("Unsigned"), mode = "Offline", tagValue = 18000000000000000000UL }),
                Call("GetTagValueULINTAsync", new { tagPath = TagPath("Unsigned"), mode = "Offline" }), Call("BuildAsync"), Call("SaveAsync")],
                SourceArtifactId: converted.Id, ExpectedSha256: converted.Sha256));
            Check(typed.Steps[1].Value!.Value.GetInt32() == 41 && typed.Steps[3].Value!.Value.GetDouble() == 1.25 &&
                typed.Steps[5].Value!.Value.GetUInt64() == 18000000000000000000UL, "DINT / LREAL / ULINT values round-trip exactly in Offline mode");
            NativeXml(typed);
            var saved = Acd(typed);
            var reopened = await Job("reopen / read / build", new([
                Call("GetTagValueDINTAsync", new { tagPath = TagPath("Input"), mode = "Offline" }),
                Call("GetTagValueULINTAsync", new { tagPath = TagPath("Unsigned"), mode = "Offline" }), Call("BuildAsync")],
                SourceArtifactId: saved.Id, ExpectedSha256: saved.Sha256));
            Check(reopened.Steps[0].Value!.Value.GetInt32() == 41 && reopened.Steps[1].Value!.Value.GetUInt64() == 18000000000000000000UL,
                "Saved native values survive reopening in a fresh SDK job");
            NativeXml(reopened);
            Check(originalHash == ProjectService.Hash(File.ReadAllBytes(service.Download(original.Id).Path)), "Generated baseline ACD remains unchanged");
            passed = true;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new { status = passed ? "passed" : "failed",
                logixMajorRevision = revision, checks, jobs, nativeBridgeSha256 = ProjectService.Hash(File.ReadAllBytes(bridgePath)),
                controllerOperationsEnabled = false, controllerTargets = 0, controllerConnectionAttempted = false,
                boundary = "Generated offline create/build/convert/local typed tags/save/reopen only. No live edits, controller, safety, HMI or runtime validation." }, ProjectService.Json));
            Console.WriteLine("Version verification evidence: " + evidence);
        }
    }
}
