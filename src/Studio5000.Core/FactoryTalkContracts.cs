namespace Studio5000.Core;

public sealed record GraphicsValidation(string Edition, string SchemaFile, string? SchemaSha256,
    bool SchemaValidated, string[] Errors);
public sealed record PrepareGraphicsImportRequest(string ExpectedSha256, string ApplicationName,
    string DisplayName, string Language);
public sealed record GraphicsProperty(string XPath, string? Attribute, string Value);
public sealed record GraphicsImportPlan(string Id, string ProjectId, string ExpectedSha256,
    string ApplicationName, string DisplayName, string Language, string Edition,
    string XmlPath, string BatchPath, string BackupPath, string ReexportPath,
    GraphicsValidation Validation, GraphicsProperty[] Changes, Artifact Package,
    string NativeImportStatus, DateTimeOffset CreatedAt);
public sealed record VerifyGraphicsImportRequest(string? ReexportPath = null);
public sealed record GraphicsPropertyResult(string XPath, string? Attribute, string Expected,
    string? Actual, bool Matches);
public sealed record GraphicsVerification(string PlanId, string ProjectId, string Status,
    string ReexportSha256, GraphicsValidation Validation, GraphicsPropertyResult[] Properties,
    string NativeImportStatus, string ContextBoundary, DateTimeOffset At);
