using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

// Optional, fail-closed restrictions for a particular controller. All clients use this check.
public static class SdkTargetPolicies
{
    private static readonly string[] SessionMethods = ["OpenLogixProjectAsync", "UploadToNewProjectAsync", "UploadAsync",
        "GoOnlineAsync", "GoOfflineAsync", "ReadConnectedStateAsync", "ReadControllerModeAsync", "GetCommunicationsPathAsync",
        "SetCommunicationsPathAsync", "SaveAsync", "SaveAsAsync", "PartialExportToXmlFileAsync"];
    private static readonly string[] ImportMethods = ["PartialImportRungsFromXmlFileAsync", "PartialImportWithTargetFromXmlFileAsync"];
    private static readonly string[] BuiltinTypes = ["BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL"];
    public static void ValidateConfiguration(ControllerTarget target)
    {
        if (target.Policy is not { } policy) return;
        if (policy.AllowedMethods is null || policy.AllowedMethods.Distinct(StringComparer.Ordinal).Count() != policy.AllowedMethods.Length)
            throw new ArgumentException("Target policy requires a distinct AllowedMethods array (empty denies all requests).");
        foreach (var name in policy.AllowedMethods) SdkCatalog.Find(name);
        if (policy.ProgramName is not null) _ = OnlineEdits.RoutinePath(policy.ProgramName, policy.RoutineName ?? "main");
        else if (policy.RoutineName is not null) throw new ArgumentException("A target RoutineName requires ProgramName.");
    }

    public static void ValidateCalls(ControllerTarget? target, SdkCall[] calls)
    {
        if (target?.Policy is not { } policy) return;
        foreach (var call in calls)
        {
            if (!policy.AllowedMethods.Contains(call.Method, StringComparer.Ordinal))
                Deny("Method is not enabled for this controller target: " + call.Method);
            if (policy.ProgramName is null) continue;
            var tagMethod = call.Method.StartsWith("GetTagValue", StringComparison.Ordinal) || call.Method.StartsWith("SetTagValue", StringComparison.Ordinal);
            if (!tagMethod && !SessionMethods.Contains(call.Method) && !ImportMethods.Contains(call.Method))
                Deny("This method cannot be limited to a program by the target policy: " + call.Method);
            if (tagMethod)
            {
                var prefix = $"Controller/Programs/Program[@Name='{policy.ProgramName}']/Tags/Tag[@Name='";
                var path = call.Arguments["tagPath"].GetString()!;
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith("']", StringComparison.Ordinal) ||
                    !Regex.IsMatch(path[prefix.Length..^2], "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
                    Deny("Tag operations require an exact base tag in the configured program; controller tags, selectors and member paths are denied.");
            }
            if (ImportMethods.Contains(call.Method) || call.Method == "PartialExportToXmlFileAsync")
            {
                if (policy.RoutineName is null) Deny("Component operations require an explicit routine in the target policy.");
                if (call.Arguments["xPath"].GetString() != OnlineEdits.RoutinePath(policy.ProgramName, policy.RoutineName!))
                    Deny("Component operation must address the exact configured program/routine.");
                if (call.Method == "PartialImportWithTargetFromXmlFileAsync" && call.Arguments["targetName"].GetString() != policy.RoutineName)
                    Deny("The imported routine cannot be renamed outside the configured scope.");
            }
        }
    }

    public static void ValidateImport(ControllerTarget? target, SdkCall call, XDocument document)
    {
        if (target?.Policy is not { ProgramName: not null } policy || !ImportMethods.Contains(call.Method)) return;
        var root = document.Root;
        if (root?.Name != "RSLogix5000Content" || root.Elements().Count() != 1) Deny("Import requires one Logix controller context.");
        var controller = root!.Element("Controller");
        if (controller is null || (string?)controller.Attribute("Use") != "Context") Deny("Controller must be context only.");
        foreach (var child in controller!.Elements())
            if (child.Name != "Programs" && !(child.Name.LocalName is "Tags" or "AddOnInstructionDefinitions" && !child.HasElements) &&
                !(child.Name == "DataTypes" && child.Elements().All(e => e.Name == "DataType" && !e.HasElements &&
                    (string?)e.Attribute("Class") == "ProductDefined" && BuiltinTypes.Contains((string?)e.Attribute("Name")))))
                Deny("Program-scoped imports cannot define controller tags, types, instructions, tasks or modules.");
        if (controller.Elements().GroupBy(e => e.Name).Any(g => g.Count() != 1)) Deny("Duplicate controller sections are not allowed.");
        var programs = controller.Element("Programs");
        if (programs is null || programs.Elements().Count() != 1) Deny("Import requires exactly one program context.");
        var program = programs!.Element("Program");
        if (program is null || (string?)program.Attribute("Name") != policy.ProgramName || (string?)program.Attribute("Use") != "Context")
            Deny("Import program does not match the configured context.");
        foreach (var child in program!.Elements())
            if (child.Name.LocalName is not ("Tags" or "Routines")) Deny("Only local tags and the selected routine may be imported.");
        if (program.Elements().GroupBy(e => e.Name).Any(g => g.Count() != 1)) Deny("Duplicate program sections are not allowed.");
        foreach (var tag in program.Element("Tags")?.Elements() ?? [])
        {
            if (tag.Name != "Tag" || (string?)tag.Attribute("TagType") != "Base" || tag.Attribute("AliasFor") is not null ||
                tag.Attribute("Usage") is { Value: not "Local" } || tag.Elements().Any(e => e.Name.LocalName is not ("Description" or "Data")))
                Deny("Imports allow base local tags only; aliases, parameters and produced/consumed tags are denied.");
        }
        var routines = program.Element("Routines");
        if (routines is null || routines.Elements().Count() != 1) Deny("Import requires exactly one routine.");
        var routine = routines!.Element("Routine");
        if (routine is null || (string?)routine.Attribute("Name") != policy.RoutineName || routine.Attribute("Type") is { Value: not "RLL" } ||
            routine.Elements("RLLContent").Count() != 1 || routine.Elements().Any(e => e.Name.LocalName is not ("RLLContent" or "Description")))
            Deny("Import must contain the exact configured ladder routine.");
        // Instruction operands still require engineering review. This is an address/definition boundary,
        // not an interpreter proving that arbitrary imported ladder cannot reference existing global data.
    }
    private static void Deny(string message) => throw new OperationException("sdk_target_policy", message);
}
