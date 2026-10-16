using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;

internal static class OnlineEditPlanIntegration
{
    public static async Task<string[]> RunAsync(string root)
    {
        // A second isolated service has a fictitious target and an inert bridge file. Controller execution stays disabled.
        var fixture = Path.Combine(Path.GetTempPath(), "OnlineEditIntegration", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var source = Path.Combine(fixture, "fixture.acd"); var import = Path.Combine(fixture, "fixture.l5x");
        await File.WriteAllTextAsync(source, "Opaque ACD fixture: planning must not open a native project or contact a controller.");
        await File.WriteAllTextAsync(import, "<RSLogix5000Content TargetType='Routine' TargetName='MainRoutine'><Controller Use='Context'><Programs><Program Name='MainProgram' Use='Context'><Routines><Routine Name='MainRoutine' Type='RLL' Use='Target'><RLLContent/></Routine></Routines></Program></Programs></Controller></RSLogix5000Content>");
        var inertBridge = Path.Combine(fixture, "never-execute.exe"); await File.WriteAllTextAsync(inertBridge, "not executable");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var url = "http://127.0.0.1:" + port;
        await File.WriteAllTextAsync(Path.Combine(fixture, "service.local.json"), JsonSerializer.Serialize(new
        {
            ServiceUrl = url, Studio5000 = new ServiceOptions { StateDirectory = Path.Combine(fixture, "state"), AllowedRoots = [fixture],
                NativeBridgePath = inertBridge, EnableControllerOperations = false, ControllerTargets = [new("plan-only", @"AB_ETH-1\192.0.2.1\Backplane\0",
                    new(["GoOnlineAsync", "ReadConnectedStateAsync", "ReadControllerModeAsync", "PartialImportRungsFromXmlFileAsync", "PartialImportWithTargetFromXmlFileAsync", "SaveAsync", "GetTagValueDINTAsync"], "MainProgram", "MainRoutine"))] },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Warning", ["Microsoft.AspNetCore"] = "Warning" } }
        }, ProjectService.Json));
        var start = new ProcessStartInfo(Path.Combine(root, "artifacts", "app", "Service", "Studio5000.Service.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["STUDIO5000_PLUGIN_ROOT"] = fixture;
        using var process = Process.Start(start) ?? throw new IOException("Could not start isolated plan service.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        var checks = new List<string>();
        void Check(bool valid, string message) { if (!valid) throw new Exception("FAIL: " + message); checks.Add(message); Console.WriteLine("PASS: " + message); }
        try
        {
            using var health = new HttpClient { BaseAddress = new Uri(url) };
            var ready = false;
            for (var i = 0; i < 100; i++)
            {
                try { ready = (await health.GetAsync("/health")).IsSuccessStatusCode; } catch (HttpRequestException) { }
                if (ready) break;
                if (process.HasExited) throw new Exception("Plan service exited: " + await error);
                await Task.Delay(100);
            }
            Check(ready, "Isolated planning service starts with controller operations disabled");
            using var client = new ServiceClient(fixture);
            var capabilities = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
            var policy = capabilities.GetProperty("controllerTargets")[0].GetProperty("policy");
            Check(policy.GetProperty("allowedMethods").GetArrayLength() == 7 && policy.GetProperty("programName").GetString() == "MainProgram",
                "Configuration binding preserves the exact target method allowlist and program scope");
            await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = Path.Combine(root, "artifacts", "app", "Mcp", "Studio5000.Mcp.exe"), Name = "Local-only online edit planning",
                EnvironmentVariables = new Dictionary<string, string?> { ["STUDIO5000_PLUGIN_ROOT"] = fixture }
            }));
            var sourceHash = ProjectService.Hash(await File.ReadAllBytesAsync(source)); var importHash = ProjectService.Hash(await File.ReadAllBytesAsync(import));
            var request = new OnlineEditPlanRequest("plan-only", "MainProgram", "MainRoutine", new(import, importHash), sourceHash, SourcePath: source, ReplaceCount: 1);
            var result = await mcp.CallToolAsync("studio_online_edit_plan", new Dictionary<string, object?> { ["request"] = request });
            Check(result.IsError != true, "MCP prepares an online-edit plan through the isolated shared service");
            var plan = JsonSerializer.Deserialize<OnlineEditPlan>(result.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
            Check(plan.SdkImportOption == "AcceptEdits" && !plan.SdkTestEditsSupported && !plan.TestEditsObserved && !plan.FinalizationObserved,
                "MCP plan distinguishes SDK acceptance from native test and assembly evidence");
            var direct = await client.PostAsync<OnlineEditPlan>("/api/sdk/online-edits/plan", request with { Workflow = "finalize_during_import" });
            Check(direct.SdkImportOption == "FinalizeEdits" && direct.StudioSteps.Any(s => s.Contains("no test interval", StringComparison.Ordinal)),
                "REST exposes explicit finalization during import without inventing a test interval");
            var routineResult = await mcp.CallToolAsync("studio_online_edit_plan", new Dictionary<string, object?>
            { ["request"] = request with { ImportKind = "routine", ReplaceCount = 0, Workflow = "finalize_during_import" } });
            var routinePlan = JsonSerializer.Deserialize<OnlineEditPlan>(routineResult.Content.OfType<TextContentBlock>().First().Text, ProjectService.Json)!;
            var routineCall = routinePlan.SdkRequest.Calls.Single(c => c.Method == "PartialImportWithTargetFromXmlFileAsync");
            Check(routineResult.IsError != true && routineCall.Arguments["xPath"].GetString() == OnlineEdits.ProgramPath("MainProgram") &&
                routineCall.Arguments["targetName"].GetString() == "MainRoutine" && routinePlan.SdkImportOption == "FinalizeEdits",
                "MCP routine replacement targets its parent program and exact routine name");
            var appendArgs = new Dictionary<string, JsonElement>(routineCall.Arguments)
            { ["xPath"] = JsonSerializer.SerializeToElement(OnlineEdits.RoutinePath("MainProgram", "MainRoutine")) };
            try
            {
                await client.PostAsync<SdkJob>("/api/sdk/jobs", routinePlan.SdkRequest with { Calls = [routineCall with { Arguments = appendArgs }] });
                throw new Exception("Append-prone routine path was accepted.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.Message.Contains("sdk_target_policy", StringComparison.Ordinal))
            { Check(true, "REST rejects the append-prone existing-routine import path before dispatch"); }
            try { await client.PostAsync<SdkJob>("/api/sdk/jobs", plan.SdkRequest); throw new Exception("Disabled controller job was accepted."); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.Message.Contains("sdk_controller_disabled", StringComparison.Ordinal))
            { Check(true, "Submitting the prepared plan still cannot bypass the disabled controller gate"); }
            var tagArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("{\"tagPath\":\"Controller/Tags/Tag[@Name='Global']\",\"mode\":\"Offline\"}")!;
            try
            {
                await client.PostAsync<SdkJob>("/api/sdk/jobs", new SdkRunRequest([new("GetTagValueDINTAsync", tagArgs)],
                    SourcePath: source, ExpectedSha256: sourceHash, ControllerTargetId: "plan-only"));
                throw new Exception("Controller tag escaped the program policy.");
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && ex.Message.Contains("sdk_target_policy", StringComparison.Ordinal))
            { Check(true, "REST rejects controller-scoped tag requests before creating a native job"); }
            var foreign = await mcp.CallToolAsync("studio_online_edit_plan", new Dictionary<string, object?> { ["request"] = request with { RoutineName = "OtherRoutine" } });
            Check(foreign.IsError == true, "MCP planning rejects another routine under the configured target policy");
            var stale = await mcp.CallToolAsync("studio_online_edit_plan", new Dictionary<string, object?> { ["request"] = request with { ExpectedSha256 = "stale" } });
            Check(stale.IsError == true, "MCP rejects a stale online-edit source hash before dispatch");
            Check((await client.GetAsync<SdkJob[]>("/api/sdk/jobs")).Length == 0 &&
                sourceHash == ProjectService.Hash(await File.ReadAllBytesAsync(source)) && importHash == ProjectService.Hash(await File.ReadAllBytesAsync(import)),
                "Planning preserves input hashes and creates no native job or connection");
            var reportDir = Path.Combine(root, "artifacts", "test-results"); Directory.CreateDirectory(reportDir);
            await File.WriteAllTextAsync(Path.Combine(reportDir, "online-edit-plan.json"), JsonSerializer.Serialize(new
            { status = "passed", controllerOperationsEnabled = false, nativeBridgeExecuted = false, controllerConnectionAttempted = false, checks, plan, direct }, ProjectService.Json));
            return checks.ToArray();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); await Task.WhenAll(output, error);
            Directory.Delete(fixture, recursive: true);
        }
    }
}
