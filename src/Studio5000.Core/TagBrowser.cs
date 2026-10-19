using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TagBrowseRequest(string ProjectId, string? ExpectedSha256 = null, string? ProgramName = null,
    string? TagName = null, string? MemberPath = null, string? DataTypeName = null, string? Query = null, int Offset = 0, int Limit = 100);
public sealed record TagBrowserNode(string Name, string Kind, string DataType, int[] Dimensions, string? ProgramName,
    string? TagName, string? MemberPath, bool Expandable, bool Watchable, string? Description = null);
public sealed record TagBrowserPage(string ProjectId, string SourceSha256, string Controller, string[] Programs,
    StructureType[] Types, TagBrowserNode[] Nodes, int Total, int Offset, int Limit, TagBrowserNode? Selection = null);
public sealed record BrowserWatchRequest(string ProjectId, string ExpectedSha256, string Name, WatchTag[] Tags,
    string Mode = "Offline", string? ControllerTargetId = null, int IntervalMs = 1000, string Backend = "Sdk", int HistoryLimit = 100);

public sealed partial class ProjectService
{
    public TagBrowserPage BrowseTags(TagBrowseRequest request)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 256 || request.Query?.Length > 200)
            throw new OperationException("browser_page", "Use a nonnegative offset, limit 1–256 and search up to 200 characters.");
        if (request.DataTypeName is not null && (request.TagName is not null || request.MemberPath is not null || request.ProgramName is not null) || request.MemberPath is not null && request.TagName is null)
            throw new OperationException("browser_selector", "Expand a tag/member or a type declaration separately.");
        var info = Get(request.ProjectId);
        if (info.Kind != "logix") throw new OperationException("browser_source", "Select a Logix project or a registered SDK upload result.");
        if (request.ExpectedSha256 is not null) Expect(info, request.ExpectedSha256);
        var doc = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(info.Id, info.Revision)));
        var controller = doc.Root!.Element("Controller")!; var types = LogixStructures.Types(doc);
        var declarations = types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        bool Expandable(string type, int[] dimensions) => dimensions.Length > 0 || declarations.ContainsKey(type);
        IEnumerable<TagBrowserNode> nodes;
        TagBrowserNode? selection = null;
        if (request.TagName is not null || request.DataTypeName is not null)
        {
            WatchValidation.MemberPath(request.MemberPath);
            var tag = request.TagName is null ? null : LogixStructures.Tag(doc, request.TagName, request.ProgramName);
            var type = request.DataTypeName ?? (string?)tag!.Attribute("DataType") ?? "Unknown";
            var dims = tag is null ? [] : LogixStructures.Dimensions((string?)tag.Attribute("Dimensions"));
            var watchable = tag is not null && (string?)tag.Attribute("TagType") == "Base";
            foreach (Match part in Regex.Matches(request.MemberPath ?? "", @"([A-Za-z_][A-Za-z0-9_]*)|\[(\d+)\]"))
            {
                if (part.Groups[2].Success)
                {
                    if (dims.Length == 0 || !int.TryParse(part.Groups[2].Value, out var index) || index >= dims[0])
                        throw new OperationException("browser_member", "Array index is outside its declared dimensions.");
                    dims = dims[1..];
                }
                else
                {
                    if (dims.Length != 0 || !declarations.TryGetValue(type, out var declaration))
                        throw new OperationException("browser_member", "No member declaration is available at this path.");
                    var member = declaration.Members.SingleOrDefault(m => m.Name == part.Value)
                        ?? throw new OperationException("browser_member", "Member is absent from the type declaration.");
                    type = member.DataType; dims = member.Dimensions; watchable &= !member.Hidden;
                }
            }
            string Path(string child) => (request.MemberPath ?? "") + (string.IsNullOrEmpty(request.MemberPath) || child.StartsWith('[') ? "" : ".") + child;
            selection = new(request.MemberPath ?? request.TagName ?? type, "selection", type, dims, request.ProgramName, request.TagName, request.MemberPath, Expandable(type, dims), watchable);
            if (dims.Length > 0)
                nodes = Enumerable.Range(0, dims[0]).Select(i => new TagBrowserNode($"[{i}]", "element", type, dims[1..], request.ProgramName, request.TagName,
                    Path($"[{i}]"), Expandable(type, dims[1..]), watchable));
            else if (declarations.TryGetValue(type, out var declaration))
                nodes = declaration.Members.Select(m => new TagBrowserNode(m.Name, "member", m.DataType, m.Dimensions, request.ProgramName, request.TagName,
                    Path(m.Name), Expandable(m.DataType, m.Dimensions), watchable && !m.Hidden,
                    m.Hidden ? "Hidden implementation member" : m.Target is not null ? $"Declared bit {m.BitNumber} in {m.Target}; values use SDK decoding" : null));
            else nodes = [];
        }
        else
        {
            nodes = controller.Descendants("Tag").Where(t => t.Parent?.Name == "Tags" && t.Parent.Parent?.Name.LocalName is "Controller" or "Program")
                .Select(t => new TagBrowserNode((string)t.Attribute("Name")!, "tag", (string?)t.Attribute("DataType") ?? "Unknown",
                    LogixStructures.Dimensions((string?)t.Attribute("Dimensions")), (string?)t.Ancestors("Program").FirstOrDefault()?.Attribute("Name"),
                    (string)t.Attribute("Name")!, null, Expandable((string?)t.Attribute("DataType") ?? "Unknown", LogixStructures.Dimensions((string?)t.Attribute("Dimensions"))),
                    (string?)t.Attribute("TagType") == "Base", t.Element("Description")?.Value ?? ((string?)t.Attribute("AliasFor") is { } alias ? "Alias for " + alias : null)))
                .Where(n => request.ProgramName is null || n.ProgramName == request.ProgramName);
        }
        if (!string.IsNullOrWhiteSpace(request.Query)) nodes = nodes.Where(n => string.Join(' ', n.Name, n.DataType, n.ProgramName, n.Description).Contains(request.Query, StringComparison.OrdinalIgnoreCase));
        var total = nodes.Count();
        return new(info.Id, info.CurrentSha256, (string?)controller.Attribute("Name") ?? info.Name,
            controller.Elements("Programs").Elements("Program").Select(p => (string)p.Attribute("Name")!).ToArray(), types,
            nodes.Skip(request.Offset).Take(request.Limit).ToArray(), total, request.Offset, request.Limit, selection);
    }

    public async Task<WatchStatus> CreateBrowserWatchAsync(BrowserWatchRequest request, CancellationToken ct = default)
    {
        var info = Get(request.ProjectId); Expect(info, request.ExpectedSha256);
        if (request.Tags is null || request.Tags.Length is < 1 or > 64) throw new OperationException("watch_invalid", "Select 1–64 tags or members.");
        var metadata = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(info.Id, info.Revision)));
        foreach (var tag in request.Tags)
        {
            WatchValidation.MemberPath(tag.MemberPath);
            if ((string?)LogixStructures.Tag(metadata, tag.TagName, tag.ProgramName).Attribute("TagType") != "Base")
                throw new OperationException("watch_alias", "Select an exact base tag instead of an alias.");
            // Resolve and validate the path, including scalar leaves, without decoding array values.
            if (BrowseTags(new(info.Id, info.CurrentSha256, tag.ProgramName, tag.TagName, tag.MemberPath, Limit: 1)).Selection?.Watchable != true)
                throw new OperationException("watch_member", "Hidden implementation members cannot be selected.");
        }
        var artifact = await ExportAsync(info.Id, new(request.ExpectedSha256), ct);
        return await SaveWatchAsync(new(request.Name, request.Tags, artifact.Sha256, SourceArtifactId: artifact.Id,
            ControllerTargetId: request.ControllerTargetId, Mode: request.Mode, IntervalMs: request.IntervalMs, HistoryLimit: request.HistoryLimit, Backend: request.Backend), ct: ct);
    }
}
