using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;

internal static class ValueMergeNativeSmoke
{
    public static async Task RunAsync(string root, string bridge)
    {
        var evidence = Path.Combine(root, "artifacts", "test-results", "value-merge-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(evidence);
        using var service = new ProjectService(new() { StateDirectory = Path.Combine(evidence, "state"), AllowedRoots = [evidence], OutputRoots = [evidence],
            NativeBridgePath = bridge, NativeTimeoutSeconds = 240, EnableControllerOperations = false, ControllerTargets = [] });
        var checks = new List<string>(); var passed = false; SdkJob? job = null;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        try
        {
            var sourceFile = Path.Combine(evidence, "source.l5x"); var targetFile = Path.Combine(evidence, "target.l5x");
            File.Copy(Path.Combine(root, "examples", "WatchDemo.L5X"), sourceFile); var doc = XmlProjects.Parse(File.ReadAllBytes(sourceFile));
            doc.Descendants("DataValueMember").First().SetAttributeValue("Value", "999");
            doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Enabled").Descendants("DataValue").Single().SetAttributeValue("Value", "0");
            doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Message").Element("Data")!.ReplaceNodes(new XCData("'Other value'"));
            doc.Descendants("Tag").Single(t => (string?)t.Attribute("Name") == "Records").Descendants("DataValueMember").First().SetAttributeValue("Value", "123"); doc.Save(targetFile);
            var a = await service.RegisterAsync(sourceFile); var b = await service.RegisterAsync(targetFile);
            var request = new TagValueMergeRequest(a.Id, a.CurrentSha256, b.Id, b.CurrentSha256, SourceProgram: "WatchProgram", DestinationProgram: "WatchProgram");
            var preview = await service.PreviewTagValueMergeAsync(request); Check(preview.CanApply && preview.Changed == 4, "Generated scalar, STRING, nested UDT and UDT-array values produce a reviewed merge");
            var merged = await service.ApplyTagValueMergeAsync(new(request, preview.ReviewSha256));
            var acd = await service.ExportAsync(merged.Id, new(merged.CurrentSha256, "acd")); Check(acd.Bytes > 0, "Merged decorated/string-only L5X converts to a new native ACD");
            job = await service.StartSdkJobAsync(new([new("BuildAsync", [])], SourceArtifactId: acd.Id, ExpectedSha256: acd.Sha256));
            var clock = Stopwatch.StartNew();
            while (job.Status is "queued" or "running")
            {
                if (clock.Elapsed > TimeSpan.FromMinutes(5)) { service.CancelSdkJob(job.Id); throw new TimeoutException("Offline reopen/build timed out; retained evidence, no automatic retry."); }
                await Task.Delay(250); job = service.SdkJob(job.Id);
            }
            Check(job.Status == "succeeded" && !job.ControllerEffectsPossible && job.ResultProject is not null, "Fresh SDK job reopens and builds the generated ACD offline");
            var expected = service.ExtractStructures(new(ProjectId: a.Id, ProgramName: "WatchProgram"));
            var actual = service.ExtractStructures(new(ProjectId: job.ResultProject!.Id, ProgramName: "WatchProgram"));
            foreach (var tag in expected.Tags)
            {
                var value = actual.Tags.Single(t => t.Name == tag.Name);
                Check(value.Quality == "snapshot" && value.Value!.ToJsonString() == tag.Value!.ToJsonString(), "Native reopened value matches source: " + tag.Name);
            }
            Check(ProjectService.Hash(File.ReadAllBytes(sourceFile)) == a.SourceSha256 && ProjectService.Hash(File.ReadAllBytes(targetFile)) == b.SourceSha256 && service.Get(a.Id).Revision == 0, "Generated original files and source revision remain unchanged"); passed = true;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "summary.json"), JsonSerializer.Serialize(new { status = passed ? "passed" : "failed", checks, job,
                nativeBridgeSha256 = ProjectService.Hash(File.ReadAllBytes(bridge)), controllerOperationsEnabled = false, controllerTargets = 0, controllerConnectionAttempted = false,
                boundary = "Generated v38 offline merge / ACD export / fresh reopen / build / decorated value readback only. Live capture, runtime and deployment untested." }, ProjectService.Json));
            Console.WriteLine("Value merge native evidence: " + evidence);
        }
    }
}
