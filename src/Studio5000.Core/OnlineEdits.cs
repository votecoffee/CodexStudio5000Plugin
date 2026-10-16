using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Studio5000.Core;

public sealed record OnlineEditPlanRequest(string ControllerTargetId, string ProgramName, string RoutineName,
    SdkInputFile ImportFile, string ExpectedSha256, string? SourcePath = null, string? SourceArtifactId = null,
    string Workflow = "accept_for_test", string ImportKind = "rungs", uint InsertPosition = 0, uint ReplaceCount = 0);
public sealed record OnlineEditPlan(string PlanSha256, ControllerTarget Target, string ProgramName, string RoutineName,
    string Workflow, string SdkImportOption, bool ControllerOperationsEnabled, bool NativeBridgeConfigured,
    bool SdkTestEditsSupported, bool TestEditsObserved, bool FinalizationObserved,
    SdkRunRequest SdkRequest, string[] StudioSteps, string CompletionEvidence);

public static partial class OnlineEdits
{
    public static object Capabilities() => new
    {
        importOptions = new[] { "LeaveEdits", "AcceptEdits", "FinalizeEdits" },
        workflows = new[] { "accept_for_test", "leave_pending", "finalize_during_import" },
        standaloneTestEdits = false, standaloneUntestEdits = false, standaloneAssembleEdits = false,
        testWorkflow = "Accept with SDK; Test/Untest and Assemble through Studio's observed native UI.",
        controllerTestModeIsTestEdits = false,
        finalization = "Choose FinalizeEdits on the initial import for API-only finalization in Run mode, with no test interval. Existing accepted edits can block further imports and require native Studio completion; there is no standalone SDK finalize/assemble call.",
        skill = "logix-online-edits", liveValidation = "Operation/version-specific; see docs/VALIDATION.md."
    };
    public static string ImportOption(string workflow) => workflow switch
    {
        "accept_for_test" => "AcceptEdits", "leave_pending" => "LeaveEdits", "finalize_during_import" => "FinalizeEdits",
        _ => throw new OperationException("online_edit_workflow", "Use accept_for_test, leave_pending or finalize_during_import.")
    };
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
    public static string ProgramPath(string program)
    {
        if (string.IsNullOrEmpty(program) || !Identifier().IsMatch(program))
            throw new OperationException("online_edit_scope", "Supply a simple Logix program identifier; selectors and wildcards are not accepted as names.");
        return $"Controller/Programs/Program[@Name='{program}']";
    }
    public static string RoutinePath(string program, string routine)
    {
        if (string.IsNullOrEmpty(program) || string.IsNullOrEmpty(routine) || !Identifier().IsMatch(program) || !Identifier().IsMatch(routine))
            throw new OperationException("online_edit_scope", "Supply simple Logix program/routine identifiers; selectors and wildcards are not accepted as names.");
        return ProgramPath(program) + $"/Routines/Routine[@Name='{routine}']";
    }
}

public sealed partial class ProjectService
{
    // Preparation reads/hash-checks files only. Submission remains the common SDK job operation.
    public async Task<OnlineEditPlan> PrepareOnlineEditAsync(OnlineEditPlanRequest request, CancellationToken ct = default)
    {
        var option = OnlineEdits.ImportOption(request.Workflow);
        var xpath = OnlineEdits.RoutinePath(request.ProgramName, request.RoutineName);
        if (request.ImportKind is not ("rungs" or "routine")) throw new OperationException("online_edit_kind", "Use rungs or routine.");
        if (request.ImportKind == "routine" && (request.InsertPosition != 0 || request.ReplaceCount != 0))
            throw new OperationException("online_edit_range", "Rung positions apply only to a rungs import.");
        var target = options.ControllerTargets.SingleOrDefault(t => t.Id == request.ControllerTargetId)
            ?? throw new OperationException("sdk_target_unknown", "Select a configured ControllerTargetId. Planning never enables or connects it.");
        if ((request.SourcePath is null) == (request.SourceArtifactId is null))
            throw new OperationException("online_edit_source", "Choose exactly one saved ACD sourcePath or sourceArtifactId, with its exact SHA-256.");
        var source = request.SourceArtifactId is not null ? Download(request.SourceArtifactId).Path : AllowedSource(request.SourcePath!);
        if (!Path.GetExtension(source).Equals(".acd", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("online_edit_source", "Use a saved native ACD matching the target controller; this planner does not re-create correlation from L5X.");
        if (!string.Equals(await SdkFileHashAsync(source, ct), request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "The online-edit source hash changed. Inspect the current saved ACD.");
        var input = request.ImportFile;
        if (input is null || (input.Path is null) == (input.ArtifactId is null))
            throw new OperationException("sdk_input_invalid", "ImportFile needs exactly one path or artifactId and its SHA-256.");
        var importPath = input.ArtifactId is not null ? Download(input.ArtifactId).Path : AllowedSource(input.Path!);
        if (Path.GetExtension(importPath).ToLowerInvariant() is not (".xml" or ".l5x"))
            throw new OperationException("sdk_input_invalid", "Choose an XML/L5X routine or rung export.");
        if (new FileInfo(importPath).Length > 64L * 1024 * 1024 || new FileInfo(source).Length > 500L * 1024 * 1024)
            throw new OperationException("file_too_large", "Online-edit plans allow a 500 MiB ACD and 64 MiB XML import.");
        if (!string.Equals(await SdkFileHashAsync(importPath, ct), input.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("stale_revision", "The import file hash changed.");
        using (var reader = XmlReader.Create(importPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64L * 1024 * 1024 }))
        {
            reader.MoveToContent();
            if (reader.LocalName != "RSLogix5000Content") throw new OperationException("sdk_input_invalid", "Import must be a Logix L5X component export.");
            while (reader.Read()) { }
        }
        Dictionary<string, JsonElement> Args(object value) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;
        // A routine is imported into its program. Targeting the existing RLL routine appends content.
        var arguments = Args(new { xPath = request.ImportKind == "routine" ? OnlineEdits.ProgramPath(request.ProgramName) : xpath,
            xmlFileToImport = "edit", onlineImportOption = option });
        var method = request.ImportKind == "rungs" ? "PartialImportRungsFromXmlFileAsync" : "PartialImportWithTargetFromXmlFileAsync";
        if (request.ImportKind == "rungs")
        {
            arguments["insertPosition"] = JsonSerializer.SerializeToElement(request.InsertPosition);
            arguments["replaceCount"] = JsonSerializer.SerializeToElement(request.ReplaceCount);
        }
        else arguments["targetName"] = JsonSerializer.SerializeToElement(request.RoutineName);
        var run = new SdkRunRequest([
            new("GoOnlineAsync", []), new("ReadConnectedStateAsync", []), new("ReadControllerModeAsync", []),
            new(method, arguments), new("SaveAsync", [])], ExpectedSha256: request.ExpectedSha256,
            SourcePath: request.SourcePath, SourceArtifactId: request.SourceArtifactId, ControllerTargetId: target.Id,
            InputFiles: new() { ["edit"] = input });
        SdkCatalog.ValidateCalls(run.Calls);
        SdkTargetPolicies.ValidateCalls(target, run.Calls);
        if (target.Policy?.ProgramName is not null)
            using (var reader = XmlReader.Create(importPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                SdkTargetPolicies.ValidateImport(target, run.Calls[3], System.Xml.Linq.XDocument.Load(reader));
        var steps = new List<string>();
        if (request.Workflow != "finalize_during_import")
        {
            steps.Add("Save the returned final.acd artifact as a new local file, open it in Studio, and confirm the exact controller/path and program/routine before reconnecting. Stop on a correlation mismatch; do not download to resolve it.");
            if (request.Workflow == "leave_pending") steps.Add("Review pending edits and accept only the authorized scope in Studio.");
            steps.Add($"Inspect all accepted edits in program {request.ProgramName}; Test Accepted Program Edits can affect every edited routine in that program. Resolve unrelated accepted edits before testing.");
            steps.Add("Use Test Accepted Program Edits in Studio and observe the native Testing Edits state. This executes changed logic; controller Test mode is a separate operation.");
            steps.Add("Validate the user's stated functional checks and monitored values. If checks fail, use the authorized Untest workflow and retain the result; do not assemble.");
            steps.Add($"After successful testing, assemble accepted edits for routine {request.RoutineName} where supported, or the explicitly authorized program scope. Observe removal of the edit/test state, then save and export the resulting project.");
        }
        else steps.Add("This request finalizes during import with no test interval. Choose it on the initial import: existing accepted edits can block a subsequent import and require native Studio completion. Inspect controller mode and native edit status; SDK finalization assembles in Run mode. SDK call success alone does not prove observed assembly or functional validation.");
        var digest = Hash(JsonSerializer.SerializeToUtf8Bytes(new { run, request.Workflow, request.ProgramName, request.RoutineName }, Json));
        return new(digest, target, request.ProgramName, request.RoutineName, request.Workflow, option,
            options.EnableControllerOperations, native.Available, false, false, false, run, steps.ToArray(),
            "Plan prepared only. SDK submission and native Studio test/assemble observations are separate; no controller state was read or changed.");
    }
}
