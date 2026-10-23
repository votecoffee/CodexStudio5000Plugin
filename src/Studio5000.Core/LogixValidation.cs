using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record NativeReadbackRules(bool NormalizeMessageFormatting = false, bool NormalizeL5KWhitespace = false, bool IgnoreRungNumbers = false, bool NormalizeRllWhitespace = false, bool NormalizeProtectedPayloadWhitespace = false, bool NormalizeAnnotationBoundaryWhitespace = false);
public sealed record LogixExpectedDifference(string Check, string Location, string? ExpectedSha256, string? ActualSha256, string Reason);
public sealed record LogixValidationRequest(string Profile, MergeSnapshot Candidate, MergeSnapshot? Baseline = null,
    MergeSnapshot? Reference = null, LogicValueMergeRequest? Merge = null, string? ExpectedPolicySha256 = null, NativeReadbackRules? NativeRules = null, LogixExpectedDifference[]? ExpectedDifferences = null);
public sealed record LogixValidationFinding(string Check, string Location, string Status, string Classification, string Detail,
    string? ExpectedSha256 = null, string? ActualSha256 = null, string? ExpectedValue = null, string? ActualValue = null, bool ValuesTruncated = false);
public sealed record LogixValidationCheck(string Name, string Status, int Compared, int Failed, int Inherited, int Expected = 0);
public sealed record LogixValidationReport(int SchemaVersion, string Profile, string ToolVersion, ComparisonSnapshot Candidate,
    ComparisonSnapshot? Reference, string PolicySha256, string ReviewSha256, string Status, LogixValidationCheck[] Checks,
    LogixValidationFinding[] Findings, string[] UnsupportedCoverage, string Boundary, LogixValidationRequest Request);
public sealed record LogixValidationRecordRequest(LogixValidationRequest Preview, string ExpectedReviewSha256);
public sealed record LogixValidationReceipt(Artifact Artifact, string ReviewSha256, string Status);
public sealed record LogixValidationDocument(int SchemaVersion, string Kind, DateTimeOffset CreatedAt, LogixValidationReport Report);

internal static class LogixValidation
{
    public const string Boundary = "Offline preservation/integrity comparison of exact managed hashes. A pass covers only the named checks, explicit normalization and exact reviewed expected differences. Native import/build provenance, safety semantics, complete compiler reference binding and runtime/physical acceptance are unsupported by these checks. No native job, controller connection or source-file mutation.";
    public static object Profiles() => new
    {
        schemaVersion = 1, toolVersion = WorkspaceIdentity.Version,
        profiles = new[] {
            new { id = "merge-preservation", purpose = "Validate reviewed logic while keeping all current saved values and configuration; exact MESSAGE exceptions only.", requires = "candidate + preserve-fresh merge policy + expectedPolicySha256", checks = new[]{"reviewed-program-routines-and-annotations","fresh-tag-identities-definitions-aliases","all-stored-representations","message-field-exceptions","untouched-module-task-safety-AOI-configuration","direct-RLL-JSR-and-main-routine-references", LadderBranchGrammar.CheckName} },
            new { id = "native-readback", purpose = "Compare an already supplied native export/readback with its intended saved project, without running native jobs.", requires = "candidate readback + reference intended project; optional explicit nativeRules", checks = new[]{"program-and-readable-AOI-routines","tag-definitions-aliases-and-all-representations","message-configuration","other-configuration","direct-RLL-JSR-and-main-routine-references", LadderBranchGrammar.CheckName} },
            new { id = "legacy-review-readback", purpose = "Review-only v35-to-v32 comparison: keep application/readable AOI code and ordinary values; report motion configuration and protected AOI envelopes/payloads separately.", requires = "candidate native v32 readback + reference intended v35 project; optional explicit nativeRules and exact expected metadata/configuration differences", checks = new[]{"target-software-controller-Local-revisions","readable-routine-code","ordinary-tag-layouts-and-values","motion-configuration","protected-AOI-envelope-and-complete-opaque-payload","other-configuration","direct-RLL-JSR-and-main-routine-references", LadderBranchGrammar.CheckName} },
            new { id = "offline-integrity", purpose = "Check RLL branch grammar and direct saved-project routine references; distinguish inherited/new/resolved findings against an optional baseline. Invalid branch grammar still fails when inherited.", requires = "candidate; optional baseline", checks = new[]{"direct-RLL-JSR-and-main-routine-references", LadderBranchGrammar.CheckName} }
        }, maximumXmlElements = 1000000, maximumFindings = 10000, maximumReceiptBytes = 8 * 1024 * 1024,
        nativeRules = new NativeReadbackRules(),
        annotationBoundaryRule = "Opt-in normalizeAnnotationBoundaryWhitespace applies only to scalar Comment/Description outside stored/encoded data. XML space/tab/CR/LF at the boundaries only; internal text, attributes and markup remain exact. Each normalization is retained as a pass/normalized finding with field hashes and bounded before/after text. Readback profiles only.",
        maximumExpectedDifferences = 1000,
        retainedReceipt = "studio_validation_record binds the complete report, reviewed policy, source/candidate hashes and tool version. Re-read with studio_validation_receipt; save with studio_save_artifact.", boundary = Boundary
    };
    private static string? Hash(XElement? node, bool rungNumbers = true) => node is null ? null : ProjectService.Hash(Encoding.UTF8.GetBytes(ProjectComparison.Normalize(node, rungNumbers).ToString(SaveOptions.DisableFormatting)));
    private static XElement ProjectConfiguration(XElement node, bool separateProtected = false)
    {
        var result = new XElement(node.Name, node.Attributes().Select(a => new XAttribute(a)));
        foreach (var content in node.Nodes())
        {
            if (content is XElement child)
            {
                if (child.Name == "Tag" && child.Parent?.Name == "Tags" && child.Parent.Parent?.Name.LocalName is "Program" or "Controller") continue;
                if (child.Name == "Routines" && child.Parent?.Name == "Program") continue;
                if (separateProtected && Protected(child)) continue;
                result.Add(ProjectConfiguration(child, separateProtected));
            }
            else if (content is XCData data) result.Add(new XCData(data.Value));
            else if (content is XText text && (!node.HasElements || !string.IsNullOrWhiteSpace(text.Value))) result.Add(new XText(text.Value));
            else if (content is XComment comment) result.Add(new XComment(comment.Value));
            else if (content is XProcessingInstruction instruction) result.Add(new XProcessingInstruction(instruction.Target, instruction.Data));
        }
        return result;
    }
    private static Dictionary<string, XElement> AllRoutines(XDocument doc) => LogixPreservation.Index(doc.Descendants("Routine").Where(r => r.Parent?.Name == "Routines" && r.Parent.Parent?.Name.LocalName is "Program" or "AddOnInstructionDefinition"),
        r => r.Parent!.Parent!.Name.LocalName + "/" + (string?)r.Parent.Parent.Attribute("Name") + "/" + (string?)r.Attribute("Name"));
    private static bool Protected(XElement node) => node.Name == "EncodedData" && node.Parent?.Name == "AddOnInstructionDefinitions" && (string?)node.Attribute("EncodedType") == "AddOnInstructionDefinition";
    private static bool Motion(XElement tag) => (string?)tag.Attribute("DataType") == "MOTION_GROUP" || ((string?)tag.Attribute("DataType"))?.StartsWith("AXIS_", StringComparison.OrdinalIgnoreCase) == true || tag.Elements("Data").Any(d => (string?)d.Attribute("Format") is "MotionGroup" or "Axis");
    private static string? ProtectedPayload(XElement? node, bool normalize)
    {
        if (node is null) return null;
        // XElement represents the text after child elements as direct XText/CDATA nodes too.
        // Child parameter/description elements belong to the separately compared envelope.
        var text = string.Concat(node.Nodes().OfType<XText>().Select(t => t.Value));
        if (string.IsNullOrWhiteSpace(text)) throw new OperationException("validation_unsupported", "Protected AOI has no supported direct opaque text/CDATA payload; nested or missing payloads are not assumed equivalent.");
        if (!normalize) return LogixPreservation.TextHash(text);
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length == 0) throw new FormatException();
            return ProjectService.Hash(bytes);
        }
        catch (FormatException) { throw new OperationException("validation_unsupported", "Protected payload whitespace normalization requires a nonempty valid base64 payload; opaque semantics are not checked."); }
    }
    private static XElement? RoutineCode(XElement? node)
    {
        if (node is null) return null;
        var copy = new XElement(node); copy.Attribute("Use")?.Remove();
        copy.Descendants().Where(e => e.Name.LocalName is "Description" or "Comment" or "LocalizedDescription" or "LocalizedComment").Remove(); return copy;
    }
    private static XElement LegacyLayout(XElement controller, string section)
    {
        var copy = LogixPreservation.LayoutContract(controller, section);
        // Export dependency inventories are reported in untouched-configuration;
        // the member/parameter declarations remain enforced as layout.
        if (section == "DataTypes") copy.Elements("DataType").Elements("Dependencies").Remove();
        return copy;
    }
    private static string L5K(string text)
    {
        var output = new StringBuilder(text.Length); char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                output.Append(c); if (c == '$' && i + 1 < text.Length) output.Append(text[++i]); else if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') { quote = c; output.Append(c); }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*') throw new OperationException("validation_unsupported", "L5K comment syntax is outside whitespace normalization.");
            else if (!char.IsWhiteSpace(c)) output.Append(c);
        }
        if (quote != '\0') throw new OperationException("validation_unsupported", "Unterminated quoted L5K data is outside whitespace normalization.");
        return output.ToString();
    }
    private static XElement Data(XElement value, NativeReadbackRules rules)
    {
        var copy = new XElement(value);
        if ((string?)copy.Attribute("Format") == "L5K" && rules.NormalizeL5KWhitespace) copy.Value = L5K(copy.Value);
        if ((string?)copy.Attribute("Format") == "Message" && rules.NormalizeMessageFormatting)
            foreach (var node in copy.Elements("MessageParameters"))
            {
                if (node.Attribute("ConnectionPath") is { } path) path.Value = string.Concat(path.Value.Where(c => !char.IsWhiteSpace(c)));
                if (node.Attribute("CacheConnections") is { } cache && bool.TryParse(cache.Value, out var flag)) cache.Value = flag ? "true" : "false";
            }
        return copy;
    }
    private static XElement? Routine(XElement? node, NativeReadbackRules rules)
    {
        if (node is null || !rules.NormalizeRllWhitespace) return node;
        var copy = new XElement(node);
        if ((string?)copy.Attribute("Type") == "RLL") foreach (var text in copy.Elements("RLLContent").Elements("Rung").Elements("Text")) text.Value = L5K(text.Value);
        return copy;
    }
    private static Dictionary<string, (string Location, string Detail)> References(XDocument doc)
    {
        var findings = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var program in doc.Root!.Element("Controller")!.Elements("Programs").Elements("Program"))
        {
            var names = program.Elements("Routines").Elements("Routine").Select(r => (string?)r.Attribute("Name")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var location = "Program/" + (string?)program.Attribute("Name"); var main = (string?)program.Attribute("MainRoutineName");
            if (!string.IsNullOrEmpty(main) && !names.Contains(main)) findings.Add(location + "/main/" + main, (location + "/@MainRoutineName", "Main routine is absent: " + main));
            foreach (var routine in program.Elements("Routines").Elements("Routine").Where(r => (string?)r.Attribute("Type") == "RLL"))
            foreach (var rung in routine.Elements("RLLContent").Elements("Rung"))
            {
                var text = (string?)rung.Element("Text") ?? "";
                // Skip quoted operands. This is direct static JSR resolution, not
                // a compiler, instruction validator or dynamic reference engine.
                var unquoted = new StringBuilder(text.Length); char quote = '\0';
                for (var i = 0; i < text.Length; i++)
                    if (quote != '\0') { if (text[i] == '$' && i + 1 < text.Length) i++; else if (text[i] == quote) quote = '\0'; unquoted.Append(' '); }
                    else if (text[i] is '\'' or '"') { quote = text[i]; unquoted.Append(' '); }
                    else unquoted.Append(text[i]);
                text = unquoted.ToString();
                foreach (Match match in Regex.Matches(text, @"(?<![A-Za-z0-9_])JSR\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*[,)]", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    if (!names.Contains(match.Groups[1].Value))
                    {
                        var path = location + "/Routine/" + (string?)routine.Attribute("Name") + "/Rung/" + ((string?)rung.Attribute("Number") ?? rung.ElementsBeforeSelf("Rung").Count().ToString());
                        findings.TryAdd(path + "/JSR/" + match.Groups[1].Value, (path, "Direct JSR target is absent: " + match.Groups[1].Value));
                    }
            }
        }
        return findings;
    }
    private static (int Compared, Dictionary<string, (string Location, string Detail, string Status, string Hash)> Findings) Branches(XDocument doc)
    {
        var findings = new Dictionary<string, (string, string, string, string)>(StringComparer.Ordinal); var compared = 0;
        foreach (var rung in doc.Descendants("Routine").Where(r => (string?)r.Attribute("Type") == "RLL").Elements("RLLContent").Elements("Rung"))
        {
            compared++; var text = (string?)rung.Element("Text") ?? "";
            try { LadderBranchGrammar.Validate(text); }
            catch (OperationException ex) when (ex.Code is "invalid_ladder" or "ladder_limit")
            {
                if (findings.Count >= 10000) throw new OperationException("validation_limit", "More than 10000 RLL branch findings; no partial report was produced.");
                var path = XmlProjects.PathOf(rung); var hash = LogixPreservation.TextHash(text)!;
                findings.Add(path + "/" + hash, (path, ex.Message, ex.Code == "ladder_limit" ? "unsupported" : "fail", hash));
            }
        }
        return (compared, findings);
    }
    public static LogixValidationReport Run(LogixValidationRequest request, ProjectInfo candidate, XDocument actual,
        ProjectInfo? reference, XDocument? expected, XDocument? reviewed, XDocument? baseline)
    {
        if (request.Profile is not ("merge-preservation" or "native-readback" or "legacy-review-readback" or "offline-integrity")) throw new OperationException("validation_profile", "Discover a named validation profile first.");
        var legacy = request.Profile == "legacy-review-readback"; var nativeComparison = request.Profile == "native-readback" || legacy;
        if (!nativeComparison && request.NativeRules is not null) throw new OperationException("validation_policy", "Native normalization choices apply only to readback comparisons.");
        var rules = request.NativeRules ?? new(); var fields = request.Merge?.ConfigurationFields ?? [];
        if (rules.NormalizeProtectedPayloadWhitespace && !legacy) throw new OperationException("validation_policy", "Protected payload normalization is available in legacy-review-readback only.");
        var exceptions = request.ExpectedDifferences ?? [];
        if (exceptions.Any(e => e?.Check == LadderBranchGrammar.CheckName))
            throw new OperationException("validation_policy", "Malformed or unsupported RLL branch grammar cannot be waived as an expected native difference.");
        if (exceptions.Length > 1000 || (!nativeComparison && exceptions.Length > 0) || exceptions.Any(e => e is null || string.IsNullOrWhiteSpace(e.Reason) || e.Reason.Length > 500 || string.IsNullOrWhiteSpace(e.Location) || e.Location.Length > 8192 || string.IsNullOrWhiteSpace(e.Check) || e.Check.Length > 128 ||
            (e.ExpectedSha256 is null && e.ActualSha256 is null) || (e.ExpectedSha256 is not null && !Regex.IsMatch(e.ExpectedSha256, "^[a-fA-F0-9]{64}$")) || (e.ActualSha256 is not null && !Regex.IsMatch(e.ActualSha256, "^[a-fA-F0-9]{64}$"))))
            throw new OperationException("validation_policy", "Use at most 1000 exact, reasoned, hash-bound native-readback differences; no wildcard approval.");
        if (legacy && exceptions.Any(e => e.Check is "target-version" or "readable-routine-code" or "ordinary-tag-layout" or "ordinary-tag-values" or "aoi-default-values" or "data-type-layouts"))
            throw new OperationException("validation_policy", "Legacy review cannot waive target revisions, code, ordinary tag layouts/values or UDT/AOI layouts as metadata/configuration exceptions.");
        var findings = new List<LogixValidationFinding>(); var counts = new Dictionary<string, int>();
        void Count(string check) => counts[check] = counts.GetValueOrDefault(check) + 1;
        void Fail(string check, string location, string detail, XElement? a, XElement? b) => findings.Add(new(check, location, "fail", "new", detail, Hash(a), Hash(b)));
        if (legacy)
        {
            Count("target-version");
            bool Version(XDocument? document, string major) => ((string?)document?.Root?.Attribute("SoftwareRevision"))?.Split('.')[0] == major && (string?)document?.Root?.Element("Controller")?.Attribute("MajorRev") == major &&
                document.Root.Element("Controller")!.Elements("Modules").Elements("Module").Count(m => (string?)m.Attribute("Name") == "Local" && (string?)m.Attribute("Major") == major) == 1;
            if (!Version(expected, "35") || !Version(actual, "32")) findings.Add(new("target-version", "SoftwareRevision/Controller.MajorRev/Local.Major", "fail", "new", "Require consistent v35 reference and actual v32 software/controller/Local native readback fields; header edits alone are not acceptance."));
        }
        void Diff(string check, string path, XElement? a, XElement? b)
        {
            if (findings.Count >= 10000) throw new OperationException("validation_limit", "More than 10000 findings; reduce the project scope. No partial receipt was produced.");
            if (Hash(a, !rules.IgnoreRungNumbers) == Hash(b, !rules.IgnoreRungNumbers)) return;
            if (a is null || b is null || a.Name != b.Name) { Fail(check, path, "Element missing, added or changed type.", a, b); return; }
            if (rules.NormalizeAnnotationBoundaryWhitespace && a.Name.Namespace == XNamespace.None && a.Name.LocalName is "Comment" or "Description" &&
                !a.Ancestors().Concat(b.Ancestors()).Any(e => e.Name.LocalName is "Data" or "DefaultData" or "ForceData" or "EncodedData") &&
                a.Nodes().All(n => n is XText) && b.Nodes().All(n => n is XText) && a.Value != b.Value && a.Value.Trim(' ', '\t', '\r', '\n') == b.Value.Trim(' ', '\t', '\r', '\n'))
            {
                Count("annotation-boundary-whitespace");
                findings.Add(new("annotation-boundary-whitespace", path + "/text()", "pass", "normalized", "Only surrounding XML space/tab/CR/LF ignored by the explicit native rule; internal text is unchanged.", Hash(a), Hash(b),
                    a.Value.Length > 512 ? a.Value[..512] : a.Value, b.Value.Length > 512 ? b.Value[..512] : b.Value, a.Value.Length > 512 || b.Value.Length > 512));
                a = new XElement(a.Name, a.Attributes(), new XText(a.Value.Trim(' ', '\t', '\r', '\n')));
                b = new XElement(b.Name, b.Attributes(), new XText(b.Value.Trim(' ', '\t', '\r', '\n')));
                if (Hash(a, !rules.IgnoreRungNumbers) == Hash(b, !rules.IgnoreRungNumbers)) return;
            }
            var aa = ProjectComparison.Normalize(a, !rules.IgnoreRungNumbers, true); var bb = ProjectComparison.Normalize(b, !rules.IgnoreRungNumbers, true);
            static string? Snippet(string? value) => value is { Length: > 512 } ? value[..512] : value;
            foreach (var name in aa.Attributes().Select(v => v.Name).Union(bb.Attributes().Select(v => v.Name)))
            {
                var before = (string?)aa.Attribute(name); var after = (string?)bb.Attribute(name);
                if (before != after) findings.Add(new(check, path + "/@" + name, "fail", "new", "Attribute differs from the reviewed preservation policy.", LogixPreservation.TextHash(before), LogixPreservation.TextHash(after), Snippet(before), Snippet(after), before?.Length > 512 || after?.Length > 512));
            }
            var beforeText = string.Concat(aa.Nodes()); var afterText = string.Concat(bb.Nodes());
            if (beforeText != afterText) findings.Add(new(check, path + "/text()", "fail", "new", "Scalar text/comment differs.", Hash(aa), Hash(bb), Snippet(beforeText), Snippet(afterText), beforeText.Length > 512 || afterText.Length > 512));
            var ac = a.Elements().ToArray(); var bc = b.Elements().ToArray();
            static string Identity(XElement e, int index)
            { var attr = e.Attribute("Name") ?? e.Attribute("Format") ?? e.Attribute("Index"); return e.Name + (attr is not null ? "[" + attr.Value + "]" : "[" + index + "]"); }
            var ak = ac.Select(Identity).ToArray(); var bk = bc.Select(Identity).ToArray();
            if (ak.Distinct(StringComparer.Ordinal).Count() != ak.Length || bk.Distinct(StringComparer.Ordinal).Count() != bk.Length)
            { Fail(check, path, "Ambiguous child identities cannot be matched.", a, b); return; }
            if (ak.ToHashSet().SetEquals(bk) && !ak.SequenceEqual(bk)) Fail(check, path + "/@child-order", "Child order changed; layout/order is significant.", a, b);
            var am = ac.Select((n, i) => (n, key: ak[i])).ToDictionary(v => v.key, v => v.n); var bm = bc.Select((n, i) => (n, key: bk[i])).ToDictionary(v => v.key, v => v.n);
            foreach (var key in ak.Union(bk)) { am.TryGetValue(key, out var av); bm.TryGetValue(key, out var bv); Diff(check, path + "/" + key, av, bv); }
        }
        if (expected is not null)
        {
            var et = LogixPreservation.Tags(expected); var at = LogixPreservation.Tags(actual);
            var reviewedTags = reviewed is null ? null : LogixPreservation.Tags(reviewed);
            if (request.Profile == "merge-preservation")
            {
                var originalTags = LogixPreservation.Tags(baseline!); var originalRoutines = LogixPreservation.Routines(baseline!);
                var reviewedRoutines = LogixPreservation.Routines(reviewed!); var freshRoutines = LogixPreservation.Routines(expected);
                Count("policy-compatibility");
                if (Hash(ProjectConfiguration(baseline!.Root!)) != Hash(ProjectConfiguration(reviewed!.Root!)))
                    findings.Add(new("policy-compatibility", "/RSLogix5000Content", "unsupported", "new", "Reviewed non-tag/routine configuration changes exceed this routine-preservation policy."));
                foreach (var key in originalTags.Keys.Union(reviewedTags!.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    originalTags.TryGetValue(key, out var old); reviewedTags.TryGetValue(key, out var modified); et.TryGetValue(key, out var fresh); Count("policy-compatibility");
                    if (LogixPreservation.TagContract(old) != LogixPreservation.TagContract(modified) || (old is not null && LogixPreservation.TagContract(old) != LogixPreservation.TagContract(fresh)))
                        findings.Add(new("policy-compatibility", key, "unsupported", "new", "Tag type, dimensions, alias, identity or safety class differs across the merge inputs."));
                }
                foreach (var section in new[] { "DataTypes", "AddOnInstructionDefinitions" })
                    if (Hash(LogixPreservation.LayoutContract(baseline.Root!.Element("Controller")!, section)) != Hash(LogixPreservation.LayoutContract(expected.Root!.Element("Controller")!, section)))
                        findings.Add(new("policy-compatibility", section, "unsupported", "new", "Fresh UDT/AOI layout differs from the common baseline."));
                foreach (var key in originalRoutines.Keys.Union(reviewedRoutines.Keys, StringComparer.OrdinalIgnoreCase).Union(freshRoutines.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    originalRoutines.TryGetValue(key, out var old); reviewedRoutines.TryGetValue(key, out var modified); freshRoutines.TryGetValue(key, out var fresh); Count("policy-compatibility");
                    if (Hash(fresh) != Hash(old) && Hash(fresh) != Hash(modified)) findings.Add(new("policy-compatibility", key, "fail", "new", "Fresh routine conflicts with the common baseline and reviewed logic."));
                    if (Hash(modified) != Hash(fresh) && new[] { old, modified, fresh }.Any(r => r is not null && LogixPreservation.Safety(r))) findings.Add(new("policy-compatibility", key, "unsupported", "new", "Safety routine edits exceed the supported preservation policy."));
                }
                foreach (var key in originalTags.Keys.Union(reviewedTags.Keys, StringComparer.OrdinalIgnoreCase).Union(et.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    var old = LogixPreservation.Message(originalTags.GetValueOrDefault(key)); var modified = LogixPreservation.Message(reviewedTags.GetValueOrDefault(key)); var fresh = LogixPreservation.Message(et.GetValueOrDefault(key));
                    foreach (var name in new[] { old, modified, fresh }.Where(n => n is not null).SelectMany(n => n!.Attributes()).Select(a => a.Name.LocalName).Distinct(StringComparer.Ordinal))
                        if ((string?)old?.Attribute(name) != (string?)modified?.Attribute(name) && (string?)modified?.Attribute(name) != (string?)fresh?.Attribute(name) && !fields.Any(f => LogixPreservation.Key(f.ProgramName, f.TagName).Equals(key, StringComparison.OrdinalIgnoreCase) && f.Attribute == name))
                            findings.Add(new("policy-compatibility", key + "/MessageParameters/@" + name, "fail", "new", "An independently changed reviewed MESSAGE field has no explicit logic/fresh choice."));
                }
                foreach (var field in fields)
                {
                    var key = LogixPreservation.Key(field.ProgramName, field.TagName); var current = et.GetValueOrDefault(key); var target = reviewedTags.GetValueOrDefault(key);
                    if (current is null || target is null || (string?)current.Attribute("DataType") != "MESSAGE" || LogixPreservation.Safety(current) ||
                        !new[] { originalTags.GetValueOrDefault(key), current, target }.Select(LogixPreservation.Message).Any(n => n?.Attribute(field.Attribute) is not null))
                        findings.Add(new("policy-compatibility", key + "/MessageParameters/@" + field.Attribute, "unsupported", "new", "MESSAGE choice has no compatible existing field/record in the pinned policy."));
                }
            }
            foreach (var key in et.Keys.Union(at.Keys, StringComparer.OrdinalIgnoreCase))
            {
                et.TryGetValue(key, out var old); at.TryGetValue(key, out var current); Count("tag-identities-definitions-aliases");
                if (legacy) Count("ordinary-tag-layout");
                if (old is null || current is null)
                { Fail("tag-identities-definitions-aliases", key, "Tag identity was added/removed.", old, current); if (legacy) Fail("ordinary-tag-layout", key, "Tag identity was added/removed.", old, current); continue; }
                Diff("tag-identities-definitions-aliases", key, LogixPreservation.Declaration(old), LogixPreservation.Declaration(current));
                if (legacy)
                {
                    if (LogixPreservation.TagContract(old) != LogixPreservation.TagContract(current)) Fail("ordinary-tag-layout", key, "Tag type/dimensions/alias/class changed.", old, current);
                }
                var left = old.Elements("Data").ToArray(); var right = current.Elements("Data").ToArray();
                var formats = left.Select(d => (string?)d.Attribute("Format") ?? "unknown").Union(right.Select(d => (string?)d.Attribute("Format") ?? "unknown"));
                foreach (var format in formats)
                {
                    var valueCheck = !legacy ? "stored-value-representations" : Motion(old) || Motion(current) ? "motion-configuration" :
                        (string?)old.Attribute("DataType") == "MESSAGE" || (string?)current.Attribute("DataType") == "MESSAGE" ? "message-configuration" : "ordinary-tag-values";
                    var av = left.Where(d => (string?)d.Attribute("Format") == format).ToArray(); var bv = right.Where(d => (string?)d.Attribute("Format") == format).ToArray(); Count(valueCheck);
                    if (av.Length != 1 || bv.Length != 1) { Fail(valueCheck, key + "/Data[" + format + "]", "Missing/duplicate representation; none was silently discarded.", av.FirstOrDefault(), bv.FirstOrDefault()); continue; }
                    XElement x; XElement y;
                    try { x = Data(av[0], rules); y = Data(bv[0], rules); }
                    catch (OperationException ex) { findings.Add(new(valueCheck, key, "unsupported", "new", ex.Message)); continue; }
                    if (request.Profile == "merge-preservation" && format == "Message")
                    {
                        foreach (var choice in fields.Where(f => LogixPreservation.Key(f.ProgramName, f.TagName).Equals(key, StringComparison.OrdinalIgnoreCase)))
                        {
                            var selected = choice.Choice == "logic" ? LogixPreservation.Message(reviewedTags!.GetValueOrDefault(key)) : x.Element("MessageParameters");
                            var wanted = (string?)selected?.Attribute(choice.Attribute); var observed = (string?)y.Element("MessageParameters")?.Attribute(choice.Attribute); Count("message-field-exceptions");
                            if (wanted != observed) findings.Add(new("message-field-exceptions", key + "/MessageParameters/@" + choice.Attribute, "fail", "new", "Exact selected MESSAGE field differs.", LogixPreservation.TextHash(wanted), LogixPreservation.TextHash(observed)));
                            x.Element("MessageParameters")?.Attribute(choice.Attribute)?.Remove(); y.Element("MessageParameters")?.Attribute(choice.Attribute)?.Remove();
                        }
                    }
                    Diff(valueCheck, key + "/Data[" + format + "]", x, y);
                }
            }
            var er = request.Profile == "merge-preservation" ? LogixPreservation.Routines(reviewed!) : AllRoutines(expected); var ar = request.Profile == "merge-preservation" ? LogixPreservation.Routines(actual) : AllRoutines(actual);
            foreach (var key in er.Keys.Union(ar.Keys, StringComparer.OrdinalIgnoreCase))
            {
                er.TryGetValue(key, out var a); ar.TryGetValue(key, out var b); Count("routines-and-annotations");
                try
                {
                    var before = Routine(a, rules); var after = Routine(b, rules); Diff("routines-and-annotations", key, before, after);
                    if (legacy) { Count("readable-routine-code"); Diff("readable-routine-code", key, RoutineCode(before), RoutineCode(after)); }
                }
                catch (OperationException ex) when (ex.Code == "validation_unsupported") { findings.Add(new("routines-and-annotations", key, "unsupported", "new", ex.Message)); }
            }
            if (legacy)
            {
                foreach (var section in new[] { "DataTypes", "AddOnInstructionDefinitions" })
                { Count("data-type-layouts"); Diff("data-type-layouts", section, LegacyLayout(expected.Root!.Element("Controller")!, section), LegacyLayout(actual.Root!.Element("Controller")!, section)); }
                static Dictionary<string, XElement> Defaults(XDocument doc) => LogixPreservation.Index(doc.Descendants("AddOnInstructionDefinition").Elements().Where(e => e.Name.LocalName is "Parameters" or "LocalTags").Elements(),
                    e => (string?)e.Parent!.Parent!.Attribute("Name") + "/" + e.Parent.Name.LocalName + "/" + (string?)e.Attribute("Name"));
                var oldDefaults = Defaults(expected); var newDefaults = Defaults(actual);
                foreach (var key in oldDefaults.Keys.Union(newDefaults.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    Count("aoi-default-values"); oldDefaults.TryGetValue(key, out var a); newDefaults.TryGetValue(key, out var b);
                    static XElement? Values(XElement? e) => e is null ? null : new XElement("Defaults", e.Elements().Where(c => c.Name.LocalName is "Data" or "DefaultData"));
                    Diff("aoi-default-values", "AOI/" + key, Values(a), Values(b));
                }
                var before = LogixPreservation.Index(expected.Descendants().Where(Protected), e => (string)e.Attribute("Name")!);
                var after = LogixPreservation.Index(actual.Descendants().Where(Protected), e => (string)e.Attribute("Name")!);
                foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    before.TryGetValue(key, out var a); after.TryGetValue(key, out var b); Count("protected-aoi-envelope"); Count("protected-aoi-payload");
                    static XElement? Envelope(XElement? e) => e is null ? null : new XElement(e.Name, e.Attributes(), e.Elements());
                    Diff("protected-aoi-envelope", "EncodedAOI/" + key, Envelope(a), Envelope(b));
                    try
                    {
                        var oldHash = ProtectedPayload(a, rules.NormalizeProtectedPayloadWhitespace); var newHash = ProtectedPayload(b, rules.NormalizeProtectedPayloadWhitespace);
                        if (oldHash != newHash || oldHash is null) findings.Add(new("protected-aoi-payload", "EncodedAOI/" + key, "fail", "new", "Complete direct opaque text/CDATA payload differs or is missing; envelope metadata is compared separately. No payload semantics inferred.", oldHash, newHash));
                    }
                    catch (OperationException ex) { findings.Add(new("protected-aoi-payload", "EncodedAOI/" + key, "unsupported", "new", ex.Message)); }
                }
            }
            Count("untouched-configuration"); Diff("untouched-configuration", "/RSLogix5000Content", ProjectConfiguration(expected.Root!, legacy), ProjectConfiguration(actual.Root!, legacy));
        }
        var inherited = baseline is null ? new Dictionary<string, (string Location, string Detail)>() : References(baseline); var currentReferences = References(actual);
        Count("direct-routine-references");
        foreach (var (key, value) in currentReferences) findings.Add(new("direct-routine-references", value.Location, "fail", inherited.ContainsKey(key) ? "inherited" : "new", value.Detail));
        foreach (var (key, value) in inherited.Where(v => !currentReferences.ContainsKey(v.Key))) findings.Add(new("direct-routine-references", value.Location, "pass", "resolved", value.Detail));
        var oldBranches = baseline is null ? null : Branches(baseline).Findings; var currentBranches = Branches(actual);
        counts[LadderBranchGrammar.CheckName] = currentBranches.Compared;
        foreach (var (key, value) in currentBranches.Findings)
            findings.Add(new(LadderBranchGrammar.CheckName, value.Location, value.Status, oldBranches?.ContainsKey(key) == true ? "inherited" : "new", value.Detail, ActualSha256: value.Hash));
        if (oldBranches is not null)
            foreach (var (key, value) in oldBranches.Where(v => !currentBranches.Findings.ContainsKey(v.Key)))
                findings.Add(new(LadderBranchGrammar.CheckName, value.Location, "pass", "resolved", value.Detail, ExpectedSha256: value.Hash));
        foreach (var exception in exceptions)
        {
            var matches = findings.Select((finding, index) => (finding, index)).Where(v => v.finding.Status == "fail" && v.finding.Classification == "new" && v.finding.Check == exception.Check && v.finding.Location == exception.Location &&
                string.Equals(v.finding.ExpectedSha256, exception.ExpectedSha256, StringComparison.OrdinalIgnoreCase) && string.Equals(v.finding.ActualSha256, exception.ActualSha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new OperationException("stale_revision", "An expected native difference is missing, duplicated or has different field hashes; review again.");
            var match = matches[0]; findings[match.index] = match.finding with { Status = "pass", Classification = "expected", Detail = "Reviewed expected difference: " + exception.Reason + ". " + match.finding.Detail };
        }
        if (findings.Count > 10000) throw new OperationException("validation_limit", "More than 10000 findings; no partial receipt was produced.");
        static bool Fails(LogixValidationFinding finding) => finding.Status == "fail" && (finding.Classification == "new" || finding.Check == LadderBranchGrammar.CheckName);
        var checks = counts.Select(k => new LogixValidationCheck(k.Key,
            findings.Any(f => f.Check == k.Key && Fails(f)) ? "fail" : findings.Any(f => f.Check == k.Key && f.Status == "unsupported") ? "unsupported" : "pass", k.Value,
            findings.Count(f => f.Check == k.Key && Fails(f)), findings.Count(f => f.Check == k.Key && f.Classification == "inherited"), findings.Count(f => f.Check == k.Key && f.Classification == "expected"))).ToArray();
        var policyHash = request.Profile == "merge-preservation" ? LogixPreservation.PolicyHash(request.Merge!) : ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { request.Profile, request.Reference, request.NativeRules, request.ExpectedDifferences }, ProjectService.Json));
        var unsupported = new[] { "Native import/build execution and provenance are not established by supplied files.", "Safety semantics, dynamic/AOI parameter binding and complete instruction/reference compilation are not checked.", "Stored representations are compared, not decoded to prove cross-encoding semantic equivalence or simultaneous live values.", "Runtime, live values and physical/commissioning acceptance are not checked." };
        if (legacy) unsupported = [..unsupported, "Protected AOI semantics are uninspected. Matching envelope metadata, matching payload bytes or explicitly reviewed re-encoding differences do not establish opaque-code equivalence."];
        var review = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, policyHash, toolVersion = WorkspaceIdentity.Version, checks, findings }, ProjectService.Json));
        static ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
        var report = new LogixValidationReport(1, request.Profile, WorkspaceIdentity.Version, Snapshot(candidate), reference is null ? null : Snapshot(reference), policyHash, review,
            checks.Any(c => c.Status == "fail") ? "fail" : checks.Any(c => c.Status == "unsupported") ? "unsupported" : "pass", checks, findings.ToArray(), unsupported, legacy ? "REVIEW ONLY v35-to-v32. " + Boundary : Boundary, request);
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 8 * 1024 * 1024) throw new OperationException("validation_limit", "Validation report exceeds 8 MiB; no partial report was produced.");
        return report;
    }
}

public sealed partial class ProjectService
{
    public object LogixValidationProfiles() => LogixValidation.Profiles();
    private LogixValidationReport BuildLogixValidation(LogixValidationRequest request)
    {
        if (request.Candidate is null || (request.Profile == "merge-preservation" && (request.Reference is not null || request.Baseline is not null)) ||
            (request.Profile != "merge-preservation" && request.Merge is not null) || (request.Profile == "offline-integrity" && request.Reference is not null))
            throw new OperationException("validation_policy", "Select an explicit candidate and only the input roles used by the chosen profile.");
        (ProjectInfo Info, XDocument Doc) Load(MergeSnapshot input)
        { var p = PreservationSource(input); var doc = XmlProjects.Parse(p.Bytes); LogixLexicalDocument.ValidateBudget(doc); return (p.Project, doc); }
        var candidate = Load(request.Candidate); ProjectInfo? reference = null; XDocument? expected = null; XDocument? reviewed = null; XDocument? baseline = null;
        if (request.Profile == "merge-preservation")
        {
            var merge = request.Merge ?? throw new OperationException("validation_policy", "Provide the reviewed preserve-fresh merge policy.");
            if (merge.Mode != "preserve-fresh" || merge.Tags is not null || merge.Resolutions is { Length: > 0 }) throw new OperationException("validation_policy", "Use the whole-project preserve-fresh policy.");
            if (!string.Equals(request.ExpectedPolicySha256, LogixPreservation.PolicyHash(merge), StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Review the merge policy hash before validation.");
            LogixPreservation.ValidateChoices(merge.ConfigurationFields ?? []);
            var fresh = Load(merge.Values); reference = fresh.Info; expected = fresh.Doc; reviewed = Load(merge.Logic).Doc; baseline = Load(merge.Baseline).Doc;
        }
        else if (request.Profile is "native-readback" or "legacy-review-readback")
        { var original = Load(request.Reference ?? throw new OperationException("validation_policy", "Provide the intended saved project as reference.")); reference = original.Info; expected = original.Doc; baseline = request.Baseline is null ? original.Doc : Load(request.Baseline).Doc; }
        else if (request.Baseline is not null) baseline = Load(request.Baseline).Doc;
        return LogixValidation.Run(request, candidate.Info, candidate.Doc, reference, expected, reviewed, baseline);
    }
    public async Task<LogixValidationReport> PreviewLogixValidationAsync(LogixValidationRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildLogixValidation(request); } finally { gate.Release(); } }
    public async Task<LogixValidationReceipt> RecordLogixValidationAsync(LogixValidationRecordRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var report = BuildLogixValidation(request.Preview);
            if (!report.ReviewSha256.Equals(request.ExpectedReviewSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Validation sources, expected changes or findings differ from the reviewed report.");
            var document = new LogixValidationDocument(1, "logix_validation", DateTimeOffset.UtcNow, report); var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);
            if (bytes.Length > 8 * 1024 * 1024) throw new OperationException("validation_limit", "Validation receipt exceeds 8 MiB; no partial receipt was produced.");
            var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
            var artifact = new Artifact(id, request.Preview.Candidate.ProjectId, "json", Hash(bytes), bytes.Length, LogixValidation.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact);
            Audit("logix_validation_record", Get(request.Preview.Candidate.ProjectId), report.Candidate.Sha256, artifact.Sha256, report.ReviewSha256);
            return new(artifact, report.ReviewSha256, report.Status);
        }
        finally { gate.Release(); }
    }
    public LogixValidationDocument LogixValidationReceipt(string id, string expectedSha256)
    {
        var (artifact, file) = Download(id);
        if (artifact.Format != "json" || !artifact.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) || artifact.Bytes > 8 * 1024 * 1024) throw new OperationException("stale_revision", "Select an exact retained validation receipt.");
        var document = Read<LogixValidationDocument>(file);
        if (document.Kind != "logix_validation" || document.SchemaVersion != 1) throw new OperationException("validation_receipt", "This artifact is not a supported validation receipt.");
        return document;
    }
}
