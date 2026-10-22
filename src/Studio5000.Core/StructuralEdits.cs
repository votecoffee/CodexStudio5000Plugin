using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record StructuralRoutineScope(string ProgramName, string RoutineName);
public sealed record StructuralTagScope(string TagName, string? ProgramName = null);
public sealed record StructuralProtection(string XPath, string ExpectedSha256);
public sealed record StructuralReferenceAddition(string Key, string Kind, string Name, SdkInputFile Source, string XPath, string ExpectedElementSha256);
public sealed record StructuralValueDisposition(StructuralTagScope Tag, string Action, string Reason, StructuralTagScope? FromTag = null);
public sealed record StructuralValueReview(StructuralTagScope Tag, string Action, string Reason, StructuralTagScope? FromTag, string? BeforeValueSha256, string? AfterValueSha256, string Status);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuralScope(StructuralRoutineScope[] Routines, StructuralTagScope[] Tags, string[] DataTypes,
    StructuralProtection[]? Protected = null, string SafetyHandling = "block", bool AcknowledgeIncompleteBindings = false, StructuralReferenceAddition[]? ReferenceAdditions = null, StructuralValueDisposition[]? ValueDispositions = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuralEdit(string Operation, string XPath, string ExpectedElementSha256, string? Value = null, string? ReferenceKey = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuralEditRequest(string ProjectId, string ExpectedSha256, int ExpectedRevision, SdkInputFile Plan,
    StructuralScope Scope, StructuralEdit[] Edits);
public sealed record StructuralContextRequest(string ProjectId, string ExpectedSha256, int ExpectedRevision, string? XPath = null, string? Query = null, int Offset = 0, int Limit = 100, string[]? XPaths = null);
public sealed record StructuralTarget(string XPath, string Kind, string? Name, string? ProgramName, string ElementSha256, bool Safety, bool Protected,
    string? DataType, string? TagType, string? ValueSetSha256);
public sealed record StructuralContext(ComparisonSnapshot Source, int Total, StructuralTarget[] Targets, string XmlPreservation, string Boundary);
public sealed record StructuralFinding(string Status, string Check, string Location, string Detail);
public sealed record StructuralChange(string Operation, string XPath, string BeforeSha256, string? FragmentSha256, string Kind, string? Name, string? ProgramName, bool Safety);
public sealed record StructuralReferenceBinding(string Key, string Kind, string Name, string SourceSha256, string ElementSha256);
internal sealed record ResolvedStructuralReference(StructuralReferenceBinding Binding, string Xml, Dictionary<string, string> TypeLayouts, string[] Aois);
public sealed record StructuralPreservation(int UntouchedTags, string UntouchedTagSetSha256, int PreservedTagValueSets, string PreservedTagValueSetSha256,
    int ProtectedSubtrees, string ProtectedSubtreeSetSha256, bool UnselectedBytesPreserved);
public sealed record StructuralEditPreview(StructuralEditRequest Request, ComparisonSnapshot Source, string PlanSha256, string ReviewSha256,
    bool CanApply, string? ResultSha256, string XmlPreservation, StructuralChange[] Changes, StructuralFinding[] Findings,
    StructuralPreservation Preservation, string Boundary, StructuralReferenceBinding[] ReferenceBindings, StructuralValueReview[] ValueDispositions, Artifact? PreviewReceipt = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StructuralApplyRequest(StructuralEditRequest Preview, string ExpectedReviewSha256, string PreviewReceiptId, string PreviewReceiptSha256);
public sealed record StructuralEditResult(ProjectInfo Project, Artifact Candidate, Artifact Receipt, string ReviewSha256, bool ReviewOnly = true, string NativeAcceptance = "not-performed");
public sealed record StructuralEditStatus(Artifact PreviewReceipt, StructuralEditPreview Preview, string Status, StructuralEditResult? Result, string Boundary);
internal sealed record StructuralPreviewDocument(string Kind, DateTimeOffset CreatedAt, StructuralEditPreview Preview,
    int? SchemaVersion = null, string? Producer = null, string? ToolVersion = null);
internal sealed record StructuralApplyState(string Status, string ReviewSha256, StructuralEditResult? Result);

internal static partial class StructuralEditing
{
    internal const string Preservation = "structural-byte-spans-v1";
    internal const string Boundary = "Offline REVIEW ONLY source-bound component batch. Routines/rungs/ST lines, exact scoped tags and UDTs plus scalar text inside their declared scopes; no Program, AOI, module, task or safety-configuration replacement. All unselected source bytes, untouched tags, retained tag representations, AOI libraries, encoded/signed subtrees and force data are preserved. The supplied plan is hash-checked, not interpreted as authority. Static references/layout checks are bounded lexical checks, not complete compiler/AOI/FBD/SFC/external/HMI binding. Safety edits require explicit review-only handling. No SDK job, native import/readback, controller access or safety/deployment acceptance. One source project per batch; separate projects are not one atomic transaction.";
    internal static bool Hash(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-fA-F0-9]{64}\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string Key(string? program, string name) => (program ?? "").ToUpperInvariant() + "/" + name.ToUpperInvariant();
    private static XElement? Program(XElement element) => element.AncestorsAndSelf("Program").FirstOrDefault();
    private static string? ProgramName(XElement element) => (string?)Program(element)?.Attribute("Name");
    private static string? Name(XElement element) => (string?)element.Attribute("Name");
    private static Dictionary<string, XElement> Tags(XDocument doc) => doc.Root!.Element("Controller")!.Descendants("Tag")
        .Where(t => t.Parent?.Name == "Tags" && t.Parent.Parent?.Name.LocalName is "Controller" or "Program").ToDictionary(t => Key(ProgramName(t), Name(t)!), StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, XElement> Types(XDocument doc) => doc.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType").ToDictionary(t => Name(t)!, StringComparer.OrdinalIgnoreCase);
    private static XElement[] Protected(XDocument doc) => doc.Descendants().Where(e =>
        e.Name.LocalName is "AddOnInstructionDefinition" or "EncodedData" or "ForceData" or "Modules" or "Tasks" or "SafetyInfo" ||
        e.Attributes().Any(a => a.Name.LocalName.Contains("Signature", StringComparison.OrdinalIgnoreCase))).Where(e => e.Name != "Controller" && e.Name != "RSLogix5000Content")
        .Where(e => !e.Ancestors().Any(a => a.Name.LocalName is "AddOnInstructionDefinition" or "Modules" or "Tasks" or "SafetyInfo")).ToArray();
    private static bool Intersects(XmlSourceDocument.Patch patch, XmlSourceDocument.Span span) => patch.Start == patch.End
        ? patch.Start > span.Start && patch.Start < span.End : patch.Start < span.End && patch.End > span.Start;
    private static string Commitment(IEnumerable<(string Key, string Hash)> values) => ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new { key = v.Key, sha256 = v.Hash }), ProjectService.Json));
    private static string Values(XmlSourceDocument document, XElement tag) => Commitment(tag.Elements().Where(e => e.Name.LocalName is "Data" or "ForceData" or "DefaultData").Select((e, i) => (i.ToString(), document.Fingerprint(e))));
    private static StructuralTarget Target(XmlSourceDocument source, XElement element, XElement[] protectedNodes) => new(XmlProjects.PathOf(element), element.Name.LocalName, Name(element), ProgramName(element), source.Fingerprint(element), LogixPreservation.Safety(element), protectedNodes.Any(p => p == element || element.Ancestors().Contains(p)), (string?)element.Attribute("DataType"), (string?)element.Attribute("TagType"), element.Name == "Tag" ? Values(source, element) : null);
    internal static StructuralContext Context(ProjectInfo project, byte[] bytes, StructuralContextRequest request, CancellationToken ct = default)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 128 || request.Query?.Length > 256 || request.XPath?.Length > 8192) throw new OperationException("structural_query", "Use offset 0..100000, limit 1..128, query <=256 and XPath <=8192 characters.");
        if (request.XPaths is { } paths && (request.XPath is not null || request.Query is not null || paths.Length is < 1 or > 128 || paths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 8192) || paths.Distinct(StringComparer.Ordinal).Count() != paths.Length))
            throw new OperationException("structural_query", "Use 1..128 distinct nonempty xPaths of <=8192 characters, without xPath or query. Each path must select exactly one existing element.");
        ct.ThrowIfCancellationRequested();
        var doc = ModuleConnectionEditing.Parse(bytes); var source = new XmlSourceDocument(bytes, doc); var protectedNodes = Protected(doc);
        XElement[] nodes;
        if (request.XPaths is { } selectors)
        {
            nodes = new XElement[selectors.Length];
            for (var index = 0; index < selectors.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                try { nodes[index] = XmlProjects.Select(doc, selectors[index]); }
                catch (System.Xml.XPath.XPathException) { throw new OperationException("structural_query", $"Invalid XPath at xPaths index {index} (zero-based). No partial context was returned."); }
                catch (OperationException ex) when (ex.Code == "ambiguous_target") { throw new OperationException("ambiguous_target", $"XPath at xPaths index {index} (zero-based) must select exactly one existing element. No partial context was returned."); }
            }
        }
        else nodes = request.XPath is not null ? new[] { XmlProjects.Select(doc, request.XPath) } : doc.Descendants().Where(e => e.Name.LocalName is "Tag" or "Routine" or "DataType").Where(e => request.Query is null || ((Name(e) ?? "") + " " + ProgramName(e)).Contains(request.Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        ct.ThrowIfCancellationRequested();
        var result = new StructuralContext(new(project.Id, project.Name, project.Revision, project.CurrentSha256), nodes.Length, nodes.Skip(request.Offset).Take(request.Limit).Select(e => Target(source, e, protectedNodes)).ToArray(), Preservation, Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(result, ProjectService.Json).Length > 4 * 1024 * 1024) throw new OperationException("structural_limit", "Context page exceeds 4 MiB.");
        return result;
    }
    private static void Policy(StructuralEditRequest request)
    {
        var scope = request.Scope;
        if (request.ExpectedRevision < 0 || scope is null || scope.Routines is null || scope.Tags is null || scope.DataTypes is null || request.Edits is null || request.Edits.Length is < 1 or > 200 || scope.SafetyHandling is not ("block" or "review-only"))
            throw new OperationException("structural_policy", "Supply revision, explicit routine/tag/type scopes, 1..200 edits and safetyHandling block or review-only.");
        if (scope.Routines.Length + scope.Tags.Length + scope.DataTypes.Length > 2000 || scope.Protected?.Length > 2000 || scope.Routines.Any(r => r is null || string.IsNullOrWhiteSpace(r.ProgramName) || string.IsNullOrWhiteSpace(r.RoutineName)) || scope.Tags.Any(t => t is null || string.IsNullOrWhiteSpace(t.TagName)) || scope.DataTypes.Any(string.IsNullOrWhiteSpace))
            throw new OperationException("structural_policy", "Use at most 2000 nonempty exact scope identities and 2000 protected paths.");
        long total = 0;
        foreach (var edit in request.Edits)
            if (edit is null || edit.Operation is not ("add_element" or "add_reference" or "remove_element" or "replace_element" or "set_text") || string.IsNullOrWhiteSpace(edit.XPath) || edit.XPath.Length > 8192 || !Hash(edit.ExpectedElementSha256) || edit.Value?.Length > 1024 * 1024 || (total += edit.Value?.Length ?? 0) > 4 * 1024 * 1024 || (edit.Operation is "remove_element" or "add_reference" && edit.Value is not null) || (edit.Operation == "add_reference" ? string.IsNullOrWhiteSpace(edit.ReferenceKey) : edit.ReferenceKey is not null))
                throw new OperationException("structural_policy", "Use exact existing-target fingerprints, supported operations, XPath <=8192, <=1 MiB per value and <=4 MiB total text; remove_element has no value.");
    }
    private static XElement Fragment(string? text, bool reference = false)
    {
        using var reader = XmlReader.Create(new StringReader(text ?? ""), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var fragment = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        if (fragment.Name.Namespace != XNamespace.None) throw new OperationException("structural_fragment", "Use an unqualified Logix component element.");
        if (!reference && fragment.DescendantsAndSelf().Any(e => e.Name.LocalName is "AddOnInstructionDefinitions" or "AddOnInstructionDefinition" or "EncodedData" or "ForceData" or "Modules" or "Module" or "Tasks" or "Task" or "SafetyInfo" || e.Attributes().Any(a => a.Name.LocalName.Contains("Signature", StringComparison.OrdinalIgnoreCase))))
            throw new OperationException("structural_protected", "New/replacement fragments cannot introduce protected libraries, signed/encoded objects, force data or hardware/safety configuration.");
        return fragment;
    }
    private static void Scope(XElement element, StructuralScope scope)
    {
        var routine = element.AncestorsAndSelf("Routine").FirstOrDefault(); var tag = element.AncestorsAndSelf("Tag").FirstOrDefault(); var type = element.AncestorsAndSelf("DataType").FirstOrDefault();
        if (routine is not null && routine.Parent?.Name == "Routines" && routine.Parent.Parent?.Name == "Program" && scope.Routines.Any(r => r.ProgramName == ProgramName(routine) && r.RoutineName == Name(routine))) return;
        if (tag is not null && tag.Parent?.Name == "Tags" && tag.Parent.Parent?.Name.LocalName is "Program" or "Controller" && scope.Tags.Any(t => t.ProgramName == ProgramName(tag) && t.TagName == Name(tag))) return;
        if (type is not null && type.Parent?.Name == "DataTypes" && type.Parent.Parent?.Name == "Controller" && scope.DataTypes.Contains(Name(type))) return;
        throw new OperationException("structural_scope", "The selected component is outside the exact routine/tag/type scope.");
    }
    private static bool Component(XElement element) => element.Name.LocalName switch
    {
        "Tag" => element.Parent?.Name == "Tags" && element.Parent.Parent?.Name.LocalName is "Controller" or "Program",
        "Routine" => element.Parent?.Name == "Routines" && element.Parent.Parent?.Name == "Program",
        "DataType" => element.Parent?.Name == "DataTypes" && element.Parent.Parent?.Name == "Controller",
        "Rung" => element.Parent?.Name == "RLLContent" && element.Parent.Parent?.Name == "Routine",
        "Line" => element.Parent?.Name == "STContent" && element.Parent.Parent?.Name == "Routine",
        _ => false
    };
    private static XElement Canonical(XElement element)
    {
        var copy = new XElement(element.Name, element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)); var text = new System.Text.StringBuilder();
        void Flush() { if (text.Length > 0) { copy.Add(new XText(text.ToString())); text.Clear(); } }
        foreach (var node in element.Nodes())
            if (node is XText value) text.Append(value.Value);
            else { Flush(); if (node is XElement child) copy.Add(Canonical(child)); else if (node is XComment comment) copy.Add(new XComment(comment.Value)); else if (node is XProcessingInstruction pi) copy.Add(new XProcessingInstruction(pi.Target, pi.Data)); }
        Flush(); return copy;
    }
    internal static string LayoutFingerprint(XElement type) => ProjectService.Hash(System.Text.Encoding.UTF8.GetBytes(new XElement("Layout",
        type.Attributes().Where(a => a.Name.LocalName is "Name" or "Class" or "Family").OrderBy(a => a.Name.ToString(), StringComparer.Ordinal), type.Elements("Members").Elements("Member").Select(m => new XElement(m.Name, m.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal)))).ToString(SaveOptions.DisableFormatting)));
    internal static (StructuralEditPreview Preview, byte[] Bytes) Build(ProjectInfo project, byte[] bytes, StructuralEditRequest request, string planSha256, ResolvedStructuralReference[] references)
    {
        Policy(request); var doc = ModuleConnectionEditing.Parse(bytes); var source = new XmlSourceDocument(bytes, doc); var expected = new XDocument(doc);
        var clone = doc.Descendants().Zip(expected.Descendants()).ToDictionary(v => v.First, v => v.Second); var protections = Protected(doc).ToList();
        foreach (var protection in request.Scope.Protected ?? [])
        {
            if (protection is null || !Hash(protection.ExpectedSha256) || string.IsNullOrWhiteSpace(protection.XPath) || protection.XPath.Length > 8192) throw new OperationException("structural_policy", "Use exact source fingerprints and bounded protected paths.");
            var node = XmlProjects.Select(doc, protection.XPath); if (!Same(source.Fingerprint(node), protection.ExpectedSha256)) throw new OperationException("stale_revision", "A declared protected subtree differs from its reviewed fingerprint."); protections.Add(node);
        }
        var protectedNodes = protections.Distinct().ToArray(); var protectedSpans = protectedNodes.Select(source.Locate).ToArray();
        var findings = new List<StructuralFinding>(); var changes = new List<StructuralChange>(); var patches = new List<XmlSourceDocument.Patch>(); var additions = new Dictionary<XElement, List<string>>();
        var touchedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var wholeTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var safety = false; var usedReferences = new HashSet<string>();
        foreach (var edit in request.Edits)
        {
            var target = XmlProjects.Select(doc, edit.XPath); var fingerprint = source.Fingerprint(target);
            if (!Same(fingerprint, edit.ExpectedElementSha256)) throw new OperationException("stale_revision", "The selected element differs from the exact reviewed source fingerprint.");
            XElement? fragment = null; XElement subject = target;
            if (edit.Operation is "add_element" or "add_reference")
            {
                var reference = edit.Operation == "add_reference" ? references.SingleOrDefault(r => r.Binding.Key == edit.ReferenceKey) ?? throw new OperationException("structural_reference", "Select one declared, hash-verified reference addition.") : null;
                var xml = reference?.Xml ?? edit.Value!; fragment = Fragment(xml, reference is not null); subject = fragment;
                // Scope classification uses the original parent's ancestry without changing the source document.
                var parent = clone[target]; var inserted = new XElement(fragment); parent.Add(inserted);
                var aoiAddition = reference is not null && reference.Binding.Kind == "aoi" && target.Name == "AddOnInstructionDefinitions" && target.Parent?.Name == "Controller";
                if (!aoiAddition && !Component(inserted)) throw new OperationException("structural_target", "Add only supported components, or exact reference AOIs to their controller container.");
                if (!aoiAddition) Scope(inserted, request.Scope);
                if (reference is not null)
                {
                    if (!usedReferences.Add(reference.Binding.Key)) throw new OperationException("structural_reference", "Each exact reference addition can be used once.");
                    if ((reference.Binding.Kind == "aoi") != aoiAddition || (reference.Binding.Kind == "udt" && inserted.Name != "DataType")) throw new OperationException("structural_reference", "Reference kind does not match its destination container.");
                    if (target.Elements().Any(e => Same(Name(e), reference.Binding.Name))) throw new OperationException("structural_reference", "Reference additions cannot replace/upgrade an existing library/type identity.");
                }
                subject = inserted;
                if (!additions.TryGetValue(target, out var values)) additions[target] = values = []; values.Add(xml.Trim(' ', '\t', '\r', '\n'));
            }
            else
            {
                Scope(target, request.Scope);
                if (edit.Operation == "set_text")
                {
                    if (target.HasElements || target.Name.LocalName is not ("Description" or "Comment" or "Text" or "Line")) throw new OperationException("structural_target", "Set text only on a scoped scalar description/comment/logic text or ST line.");
                    XmlConvert.VerifyXmlChars(edit.Value ?? ""); var value = (edit.Value ?? "").Replace("\r\n", "\n").Replace('\r', '\n'); clone[target].ReplaceNodes(new XCData(value));
                    var span = source.Locate(target); var content = "<![CDATA[" + (edit.Value ?? "").Replace("]]>", "]]]]><![CDATA[>") + "]]>";
                    patches.Add(span.Empty ? new(span.OpenEnd - 2, span.OpenEnd, ">" + content + "</" + span.QualifiedName + ">") : new(span.OpenEnd, span.CloseStart, content));
                }
                else
                {
                    if (!Component(target)) throw new OperationException("structural_target", "Replace/remove only exact tags, program routines, UDTs, RLL rungs or ST lines.");
                    var span = source.Locate(target);
                    if (edit.Operation == "replace_element")
                    {
                        fragment = Fragment(edit.Value);
                        if (fragment.Name != target.Name || (string?)fragment.Attribute("Name") != Name(target) || (string?)fragment.Attribute("Number") != (string?)target.Attribute("Number")) throw new OperationException("identity_change", "Replacement must retain component type, Name and Number; use separate remove/add for new identities.");
                        clone[target].ReplaceWith(new XElement(fragment)); patches.Add(new(span.Start, span.End, edit.Value!.Trim(' ', '\t', '\r', '\n')));
                    }
                    else { clone[target].Remove(); patches.Add(new(span.Start, span.End, "")); }
                }
            }
            var tag = subject.AncestorsAndSelf("Tag").FirstOrDefault(); if (tag is not null) touchedTags.Add(Key(ProgramName(tag), Name(tag)!));
            if (subject.Name == "Tag" && edit.Operation == "add_element") wholeTags.Add(Key(ProgramName(subject), Name(subject)!));
            if (target.Name == "Tag" && edit.Operation is "replace_element" or "remove_element") wholeTags.Add(Key(ProgramName(target), Name(target)!));
            safety |= LogixPreservation.Safety(subject) || (fragment is not null && fragment.DescendantsAndSelf().Any(LogixPreservation.Safety));
            changes.Add(new(edit.Operation, edit.XPath, fingerprint, edit.Value is null ? null : ProjectService.Hash(System.Text.Encoding.UTF8.GetBytes(edit.Value)), subject.Name.LocalName, Name(subject), ProgramName(subject), LogixPreservation.Safety(subject)));
        }
        foreach (var (parent, values) in additions)
        {
            var span = source.Locate(parent); var text = string.Concat(values);
            patches.Add(span.Empty ? new(span.OpenEnd - 2, span.OpenEnd, ">" + text + "</" + span.QualifiedName + ">") : new(span.CloseStart, span.CloseStart, text));
        }
        if (patches.Any(p => protectedSpans.Any(s => Intersects(p, s)))) throw new OperationException("structural_protected", "An edit overlaps a protected library, signed/encoded object, force data or declared protected subtree.");
        var output = source.Apply(patches); var actual = ModuleConnectionEditing.Parse(output); XmlProjects.Validate(expected);
        if (!XNode.DeepEquals(Canonical(expected.Root!), Canonical(actual.Root!))) throw new OperationException("structural_preservation", "Raw patches differ from independent semantic edit readback.");
        var readback = new XmlSourceDocument(output, actual); var beforeTags = Tags(doc); var afterTags = Tags(actual);
        var untouched = beforeTags.Where(t => !touchedTags.Contains(t.Key)).ToArray();
        foreach (var (key, tag) in untouched) if (!afterTags.TryGetValue(key, out var observed) || source.Fingerprint(tag) != readback.Fingerprint(observed)) throw new OperationException("structural_preservation", "An untouched tag changed bytes or disappeared.");
        var preservedValues = beforeTags.Where(t => !wholeTags.Contains(t.Key)).ToArray();
        foreach (var (key, tag) in preservedValues) if (!afterTags.TryGetValue(key, out var observed) || Values(source, tag) != Values(readback, observed)) throw new OperationException("structural_preservation", "A retained tag's saved representations changed without a whole-tag edit.");
        var dispositions = request.Scope.ValueDispositions ?? [];
        if (dispositions.Length > 2000 || dispositions.Any(d => d is null || d.Tag is null || string.IsNullOrWhiteSpace(d.Tag.TagName) || d.Tag.TagName.Length > 256 || d.FromTag is not null && (string.IsNullOrWhiteSpace(d.FromTag.TagName) || d.FromTag.TagName.Length > 256) || string.IsNullOrWhiteSpace(d.Reason) || d.Reason.Length > 2000 || d.Action is not ("remove" or "preserve" or "replace-reviewed" or "initialize-reviewed" or "copy-values")) || dispositions.Select(d => Key(d.Tag.ProgramName, d.Tag.TagName)).Distinct().Count() != dispositions.Length)
            throw new OperationException("structural_values", "Use unique explicit tag-value dispositions with nonempty reasons: remove, preserve, replace-reviewed, initialize-reviewed or copy-values.");
        var valueReviews = new List<StructuralValueReview>();
        foreach (var key in wholeTags)
            if (!dispositions.Any(d => Key(d.Tag.ProgramName, d.Tag.TagName) == key)) findings.Add(new("blocked", "value-disposition", key, "Every whole-tag removal/replacement/addition requires a reviewed saved-state disposition and reason; no timer/action initialization is inferred."));
        foreach (var disposition in dispositions)
        {
            if (!request.Scope.Tags.Contains(disposition.Tag)) throw new OperationException("structural_scope", "Value dispositions must select explicitly scoped tags.");
            var key = Key(disposition.Tag.ProgramName, disposition.Tag.TagName); oldTagsLookup(disposition.FromTag ?? disposition.Tag, out var old); afterTags.TryGetValue(key, out var current);
            var beforeValue = old is null ? null : Values(source, old); var afterValue = current is null ? null : Values(readback, current);
            bool SameLayout() => old is not null && current is not null && Same((string?)old.Attribute("DataType"), (string?)current.Attribute("DataType")) && (string?)old.Attribute("Dimensions") == (string?)current.Attribute("Dimensions");
            var valid = disposition.Action switch
            {
                "remove" => beforeTags.ContainsKey(key) && current is null && disposition.FromTag is null,
                "preserve" => beforeTags.ContainsKey(key) && current is not null && disposition.FromTag is null && SameLayout() && beforeValue == afterValue,
                "replace-reviewed" => beforeTags.ContainsKey(key) && current is not null && disposition.FromTag is null,
                "initialize-reviewed" => !beforeTags.ContainsKey(key) && current is not null && (disposition.FromTag is null || old is not null),
                "copy-values" => !beforeTags.ContainsKey(key) && current is not null && disposition.FromTag is not null && SameLayout() && beforeValue == afterValue,
                _ => false
            };
            if (!valid) findings.Add(new("blocked", "value-disposition", key, "The declared saved-state disposition does not match source/result presence, layout or raw stored values."));
            valueReviews.Add(new(disposition.Tag, disposition.Action, disposition.Reason, disposition.FromTag, beforeValue, afterValue, valid ? "reviewed-declaration" : "blocked"));
        }
        void oldTagsLookup(StructuralTagScope selected, out XElement? tag) => beforeTags.TryGetValue(Key(selected.ProgramName, selected.TagName), out tag);
        if (usedReferences.Count != references.Length) throw new OperationException("structural_reference", "Every declared exact reference addition must be selected by the batch.");
        var afterTypes = Types(actual); var afterAoiNames = actual.Root!.Element("Controller")!.Elements("AddOnInstructionDefinitions").Elements().Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            foreach (var (type, layout) in reference.TypeLayouts)
                if (!afterTypes.TryGetValue(type, out var definition)) findings.Add(new("blocked", "reference-type-dependency", reference.Binding.Name, "Required reference UDT is absent after the batch: " + type));
                else if (LayoutFingerprint(definition) != layout) findings.Add(new("blocked", "reference-layout-mismatch", reference.Binding.Name, "Existing/added UDT layout differs from the exact reference dependency: " + type + ". No library upgrade or struct resizing is inferred."));
            foreach (var aoi in reference.Aois.Where(t => !afterAoiNames.Contains(t))) findings.Add(new("blocked", "reference-aoi-dependency", reference.Binding.Name, "Required reference AOI is absent after the batch: " + aoi));
        }
        Dependencies(doc, actual, wholeTags, findings);
        if (safety && request.Scope.SafetyHandling != "review-only") findings.Add(new("blocked", "safety", "scope", "Safety-class edits require explicit review-only handling; signatures remain retained and unvalidated."));
        if (!request.Scope.AcknowledgeIncompleteBindings) findings.Add(new("blocked", "binding-coverage", "scope", "Explicitly acknowledge incomplete compiler/AOI/FBD/SFC/external/HMI binding coverage before creating a review copy."));
        findings.Add(new("review", "native-readback", "project", "Native compile/import/readback and safety acceptance have not been performed. Source plan bytes are hash-checked without interpreting the plan's authority."));
        if (findings.Count > 2000) throw new OperationException("structural_limit", "More than 2000 dependency findings; no partial review was produced.");
        var preservation = new StructuralPreservation(untouched.Length, Commitment(untouched.Select(t => (t.Key, source.Fingerprint(t.Value)))), preservedValues.Length, Commitment(preservedValues.Select(t => (t.Key, Values(source, t.Value)))), protectedNodes.Length, Commitment(protectedNodes.Select(n => (XmlProjects.PathOf(n), source.Fingerprint(n)))), true);
        var resultHash = ProjectService.Hash(output); var canApply = findings.All(f => f.Status != "blocked");
        var bindings = references.Select(r => r.Binding).ToArray();
        var review = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, planSha256, sourceSha256 = project.CurrentSha256, resultHash, changes, findings, preservation, bindings, valueReviews, toolVersion = WorkspaceIdentity.Version }, ProjectService.Json));
        var preview = new StructuralEditPreview(request, new(project.Id, project.Name, project.Revision, project.CurrentSha256), planSha256, review, canApply, canApply ? resultHash : null, Preservation, changes.ToArray(), findings.ToArray(), preservation, Boundary, bindings, valueReviews.ToArray());
        if (JsonSerializer.SerializeToUtf8Bytes(preview, ProjectService.Json).Length > 8 * 1024 * 1024) throw new OperationException("structural_limit", "Structural review exceeds 8 MiB.");
        return (preview, output);
    }
}
