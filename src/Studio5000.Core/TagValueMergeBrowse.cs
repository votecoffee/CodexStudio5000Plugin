using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TagValueMergeBrowseRequest(string SourceProjectId, string SourceSha256, string DestinationProjectId, string DestinationSha256,
    string? SourceProgram = null, string? DestinationProgram = null, string? Query = null, int Offset = 0, int Limit = 50);
public sealed record TagValueMergePage(ProjectInfo Source, ProjectInfo Destination, int Total, int Offset, int Limit, TagValueMergeRow[] Rows, string Coverage);

public sealed partial class ProjectService
{
    private (ProjectInfo Source, XDocument SourceDoc, ProjectInfo Destination, XDocument TargetDoc) ValueMergeDocuments(string sourceId, string sourceHash, string destinationId, string destinationHash)
    {
        if (sourceId == destinationId) throw new OperationException("merge_same_project", "Choose different source and destination projects.");
        var source = Get(sourceId); var destination = Get(destinationId); Expect(source, sourceHash); Expect(destination, destinationHash);
        if (source.Kind != "logix" || destination.Kind != "logix") throw new OperationException("not_logix", "Select Logix snapshots.");
        XDocument Document(ProjectInfo project)
        {
            var bytes = File.ReadAllBytes(RevisionFile(project.Id, project.Revision));
            if (Hash(bytes) != project.CurrentSha256) throw new OperationException("state_tampered", "Snapshot bytes changed while reading.");
            return XmlProjects.Parse(bytes);
        }
        return (source, Document(source), destination, Document(destination));
    }
    public async Task<TagValueMergePage> BrowseTagValueMergeAsync(TagValueMergeBrowseRequest request, CancellationToken ct = default)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 100 || request.Query?.Length > 256)
            throw new OperationException("merge_browse_query", "Use offset 0..100000, limit 1..100 and query up to 256 characters.");
        if (request.SourceProgram is not null) WatchValidation.Identifier(request.SourceProgram);
        if (request.DestinationProgram is not null) WatchValidation.Identifier(request.DestinationProgram);
        await gate.WaitAsync(ct);
        try
        {
            var (source, doc, destination, target) = ValueMergeDocuments(request.SourceProjectId, request.SourceSha256, request.DestinationProjectId, request.DestinationSha256);
            var candidates = TagValueMerging.Tags(doc, request.SourceProgram)
                .Where(t => string.IsNullOrEmpty(request.Query) || (((string?)t.Attribute("Name") ?? "") + " " + (string?)t.Attribute("DataType")).Contains(request.Query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => (string?)t.Attribute("Name"), StringComparer.OrdinalIgnoreCase).ToArray();
            var rows = candidates.Skip(request.Offset).Take(request.Limit).Select(t =>
            {
                ct.ThrowIfCancellationRequested(); var name = (string)t.Attribute("Name")!;
                return TagValueMerging.Inspect(doc, target, new(name, request.SourceProgram, name, request.DestinationProgram)).Row;
            }).ToArray();
            var page = new TagValueMergePage(source, destination, candidates.Length, request.Offset, request.Limit, rows,
                "Read-only candidate page; no project/draft/artifact mutation or controller connection. Same-name tags in explicit scopes, filtered by source name/type. Select up to 256 tags across pages, then request a new hash-bound merge preview before applying.");
            if (JsonSerializer.SerializeToUtf8Bytes(page, Json).Length > 3 * 1024 * 1024) throw new OperationException("merge_limit", "Candidate page exceeds 3 MiB; reduce the page limit.");
            return page;
        }
        finally { gate.Release(); }
    }
}
