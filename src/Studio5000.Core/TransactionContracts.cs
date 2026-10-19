using System.Text.Json.Serialization;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceTransactionBeginRequest(string WorkspaceId, string WorkspaceType, string ExpectedSha256, string? Name = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceTransactionStageRequest(int ExpectedVersion, Edit[]? XmlEdits = null, DocumentationEdit[]? DocumentationEdits = null,
    HmiLanguageEdit[]? LanguageEdits = null, string[]? AddLanguages = null, ViewDesignerEdit[]? ViewDesignerEdits = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceTransactionVersionRequest(int ExpectedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceTransactionCommitRequest(int ExpectedVersion, string ExpectedReviewSha256);
public sealed record WorkspaceTransactionStep(int Version, string BeforeSha256, string AfterSha256, string RequestSha256, int Edits);
public sealed record WorkspaceTransactionInfo(string Id, string WorkspaceId, string WorkspaceType, string Name, string Status,
    int BaseRevision, string BaseSha256, string StagedSha256, int Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    WorkspaceTransactionStep[] Steps, string? ReviewSha256 = null, int? CommittedRevision = null, DateTimeOffset? CommittedAt = null);
public sealed record WorkspaceTransactionPreview(WorkspaceTransactionInfo Transaction, string ReviewSha256, bool CanCommit,
    int CurrentRevision, string CurrentSha256, WorkspaceTransactionStageRequest[] Stages, string Boundary);
public sealed record WorkspaceTransactionPage(WorkspaceTransactionInfo[] Transactions, int Total, int Offset, int Limit);
