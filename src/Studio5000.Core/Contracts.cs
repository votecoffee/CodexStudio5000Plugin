namespace Studio5000.Core;

public sealed class ServiceOptions
{
    public string StateDirectory { get; set; } = ".local/state";
    public string[] AllowedRoots { get; set; } = ["examples"];
    public string[] OutputRoots { get; set; } = ["artifacts/exports"];
    public string? NativeBridgePath { get; set; }
    public int NativeTimeoutSeconds { get; set; } = 180;
    public string FactoryTalkSchemaDirectory { get; set; } = @"C:\Program Files (x86)\Rockwell Software\RSView Enterprise\Schemas";
    public string FactoryTalkStudioPath { get; set; } = @"C:\Program Files (x86)\Rockwell Software\RSView Enterprise\Launcher.exe";
}

public sealed record ProjectInfo(string Id, string Name, string SourcePath, string SourceSha256,
    string CurrentSha256, int Revision, DateTimeOffset CreatedAt, string Kind = "logix");
public sealed record RegisterRequest(string Path);
public sealed record ElementInfo(string XPath, string Kind, string? Name, string? Program);
public sealed record Inspection(ProjectInfo Project, IReadOnlyList<ElementInfo> Elements, int Total, int Offset);
public sealed record ElementContent(string XPath, string Xml, string CurrentSha256);
public sealed record Edit(string Operation, string XPath, string? Value = null, string? Attribute = null);
public sealed record PreviewRequest(string ExpectedSha256, Edit[] Edits);
public sealed record ChangePreview(string Id, string ProjectId, string BaseSha256, string ResultSha256,
    Edit[] Edits, string Diff, DateTimeOffset CreatedAt);
public sealed record ApplyRequest(string ExpectedSha256);
public sealed record ExportRequest(string ExpectedSha256, string Format = "l5x");
public sealed record SaveArtifactRequest(string Path);
public sealed record SavedArtifact(string Path, string Sha256, long Bytes);
public sealed record Artifact(string Id, string ProjectId, string Format, string Sha256, long Bytes,
    string Validation);
public sealed record AuditEvent(DateTimeOffset At, string Action, string ProjectId, string? Before,
    string? After, string Detail);
public sealed class OperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
