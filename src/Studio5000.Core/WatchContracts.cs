using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Studio5000.Core;

public sealed record WatchTag(string Key, string TagName, string? ProgramName = null, string? MemberPath = null);
public sealed record WatchListRequest(string Name, WatchTag[] Tags, string ExpectedSha256, string? SourcePath = null,
    string? SourceArtifactId = null, string? ControllerTargetId = null, string Mode = "Online", int IntervalMs = 1000, int HistoryLimit = 100, string Backend = "Sdk");
public sealed record WatchDefinition(string Id, int Revision, DateTimeOffset CreatedAt, WatchListRequest Settings);
public sealed record WatchUpdateRequest(WatchListRequest Settings, int ExpectedRevision);
public sealed record WatchValue(string Key, string TagName, string? ProgramName, string DataType, JsonNode? Value, string Quality, string? Error = null);
public sealed record WatchSnapshot(long Sequence, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string Mode, WatchValue[] Values);
public sealed record WatchStatus(WatchDefinition Definition, string State, WatchSnapshot? Latest, bool Stale, string? Error, StructureType[] Types);
public sealed record WatchBridgeOpen(string WorkDirectory, string SourcePath, string? CommunicationsPath, string Mode,
    string Backend = "Sdk", CipReadTarget? CipTarget = null, WatchTag[]? AllowedTags = null);
public sealed record WatchBridgeCommand(WatchTag[] Tags);
public sealed record WatchBridgeReply(bool Success, StructuredTag[] Tags, string? Error = null, WatchValue[]? SelectedValues = null);
public interface IWatchSession : IAsyncDisposable
{
    Task<WatchBridgeReply> ReadAsync(WatchTag[] tags, CancellationToken ct);
}
public interface IWatchSessionFactory
{
    Task<IWatchSession> OpenAsync(WatchBridgeOpen request, CancellationToken ct);
}

public static class WatchValidation
{
    public static void MemberPath(string? path)
    {
        if (path is { Length: > 256 } || path is { Length: > 0 } && !Regex.IsMatch(path,
            @"^(?:[A-Za-z_][A-Za-z0-9_]*|\[\d+\])(?:\[\d+\])*(?:\.[A-Za-z_][A-Za-z0-9_]*(?:\[\d+\])*)*$"))
            throw new OperationException("watch_member", "Use member names, dots and numeric array indices only (maximum 256 characters).");
    }
    public static void Identifier(string name)
    {
        if (string.IsNullOrEmpty(name) || !Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new OperationException("watch_tag", "Use an exact base tag/program name; selectors, aliases and member expressions are separate from the tag name.");
    }
    public static string TagPath(WatchTag tag)
    {
        Identifier(tag.TagName); if (tag.ProgramName is not null) Identifier(tag.ProgramName);
        return (tag.ProgramName is null ? "Controller" : OnlineEdits.ProgramPath(tag.ProgramName)) + $"/Tags/Tag[@Name='{tag.TagName}']";
    }
    public static void Validate(WatchListRequest request)
    {
        if (request.Backend is not ("Sdk" or "LibPlcTag") || request.Backend == "LibPlcTag" && request.Mode != "Online")
            throw new OperationException("watch_backend", "Use Sdk (Online/Offline) or LibPlcTag (Online only).");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100 || request.Tags is null || request.Tags.Length is < 1 or > 64 ||
            request.Tags.Any(t => t is null || string.IsNullOrWhiteSpace(t.Key) || t.Key.Length > 100) || request.Tags.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != request.Tags.Length)
            throw new OperationException("watch_invalid", "Supply a name and 1–64 tags with unique nonempty keys (100 characters maximum).");
        if (request.Mode is not ("Online" or "Offline") || request.IntervalMs is < 100 or > 86400000 || request.HistoryLimit is < 1 or > 1000)
            throw new OperationException("watch_invalid", "Use Online/Offline, interval 100–86400000 ms, and history limit 1–1000.");
        if ((request.SourcePath is null) == (request.SourceArtifactId is null) || !Regex.IsMatch(request.ExpectedSha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new OperationException("watch_source", "Choose exactly one sourcePath/sourceArtifactId and its exact SHA-256.");
        if (request.Mode == "Online" && string.IsNullOrWhiteSpace(request.ControllerTargetId))
            throw new OperationException("watch_target", "An online list requires a configured controller target.");
        foreach (var tag in request.Tags)
        {
            _ = TagPath(tag);
            MemberPath(tag.MemberPath);
        }
    }
}
