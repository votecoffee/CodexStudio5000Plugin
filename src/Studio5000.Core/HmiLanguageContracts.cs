namespace Studio5000.Core;

public sealed record HmiLanguageRegisterRequest(string Path, string Platform, string? Language = null);
public sealed record HmiLanguageInfo(string Id, string Name, string Platform, string Format, string SourcePath, string SourceSha256,
    string CurrentSha256, int Revision, DateTimeOffset CreatedAt, string? Language);
public sealed record HmiLanguageQuery(string ExpectedSha256, string? Query = null, string? Language = null, int Offset = 0, int Limit = 100);
public sealed record HmiLanguageText(string Language, string Text, bool Writable);
public sealed record HmiLanguageRecord(string Key, string Sheet, string[] Context, HmiLanguageText[] Texts);
public sealed record HmiLanguagePage(HmiLanguageInfo Workspace, string[] Languages, HmiLanguageRecord[] Records, int Total, int Offset, int Limit, string Boundary);
public sealed record HmiLanguageEdit(string Key, string Language, string Text);
public sealed record HmiLanguagePreviewRequest(string ExpectedSha256, HmiLanguageEdit[] Edits, string[]? AddLanguages = null);
public sealed record HmiLanguageChange(string Key, string Language, string? Before, string After);
public sealed record HmiLanguagePreview(string WorkspaceId, int Revision, string SourceSha256, string ReviewSha256, string ResultSha256,
    HmiLanguageChange[] Changes, string[] AddedLanguages, string Boundary);
public sealed record HmiLanguageApplyRequest(HmiLanguagePreviewRequest Preview, string ExpectedReviewSha256);
public sealed record HmiLanguageExportRequest(string ExpectedSha256);
public sealed record HmiLanguageVerifyRequest(string ExpectedSha256, string Path);
public sealed record HmiLanguageDifference(string Key, string? Language, string Kind, string? Expected, string? Actual);
public sealed record HmiLanguageVerification(string WorkspaceId, string ExpectedSha256, string ObservedSha256, bool Matches,
    int DifferenceCount, HmiLanguageDifference[] Differences, string Boundary);
