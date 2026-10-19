namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public GraphicsReferenceReport GraphicsTagReferences(string id, GraphicsReferenceRequest request)
    {
        var project = RequireGraphics(id);
        Expect(project, request.ExpectedSha256);
        var document = FactoryTalkGraphics.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision, project.Kind)));
        return GraphicsReferences.Extract(project, document, request);
    }
}
