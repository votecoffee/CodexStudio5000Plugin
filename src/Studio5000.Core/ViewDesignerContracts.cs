namespace Studio5000.Core;

public sealed record HmiProperty(string Selector, string Value);
public sealed record HmiObject(string Selector, string Type, string? Name);
public sealed record HmiDocument(string Text, HmiObject[] Objects, HmiProperty[] Properties);
public sealed record ViewDesignerFile(string Path, string Sha256, long Bytes, bool Editable);
public sealed record ViewDesignerInspection(ProjectInfo Project, ViewDesignerFile[] Files);
public sealed record ViewDesignerContent(string Path, string Sha256, string CurrentSha256, HmiDocument Document);
// Values use View Designer syntax, including quotes and optional -> binding expressions.
public sealed record ViewDesignerEdit(string File, string Selector, string Value);
public sealed record ViewDesignerPreviewRequest(string ExpectedSha256, ViewDesignerEdit[] Edits);
public sealed record ViewDesignerPreview(string Id, string ProjectId, string BaseSha256, string ResultSha256,
    ViewDesignerEdit[] Edits, string Diff, DateTimeOffset CreatedAt);
public sealed record ViewDesignerImportRequest(string ExpectedSha256, string TargetProject, string PanelType, string Language);
public sealed record ViewDesignerImportPlan(string Id, string ProjectId, string ExpectedSha256, string TargetProject,
    string PanelType, string Language, string ImportEntryPath, string ReexportDirectory, ViewDesignerEdit[] Changes,
    Artifact Package, string NativeImportStatus, DateTimeOffset CreatedAt);
public sealed record ViewDesignerVerification(string PlanId, string Status, string ReexportSha256,
    ViewDesignerPropertyResult[] Properties, string NativeImportStatus, string ContextBoundary, DateTimeOffset At);
public sealed record ViewDesignerPropertyResult(string File, string Selector, string Expected, string? Actual, bool Matches);
