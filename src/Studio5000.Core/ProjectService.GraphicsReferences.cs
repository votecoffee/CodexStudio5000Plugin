namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public GraphicsResolutionReport ResolveGraphicsReferences(string id, GraphicsResolveRequest request)
    {
        GraphicsReferenceResolution.Validate(request);
        var project = RequireGraphics(id); Expect(project, request.ExpectedSha256);
        var snapshots = new Dictionary<string, GraphicsPlcSnapshot>(StringComparer.OrdinalIgnoreCase);
        var loaded = new Dictionary<string, GraphicsPlcSnapshot>(StringComparer.Ordinal);
        foreach (var binding in request.Shortcuts)
        {
            var plc = Get(binding.ProjectId); Expect(plc, binding.ExpectedSha256);
            if (plc.Kind != "logix") throw new OperationException("not_logix", "Every shortcut must target a registered Logix snapshot.");
            if (!loaded.TryGetValue(plc.Id, out var snapshot))
            {
                snapshot = new(plc, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(plc.Id, plc.Revision)))); loaded.Add(plc.Id, snapshot);
            }
            Expect(snapshot.Project, binding.ExpectedSha256);
            snapshots.Add(binding.Shortcut, snapshot);
        }
        var document = FactoryTalkGraphics.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision, project.Kind)));
        return GraphicsReferenceResolution.Resolve(project, document, request, snapshots);
    }

    public GraphicsReferenceReport GraphicsTagReferences(string id, GraphicsReferenceRequest request)
    {
        var project = RequireGraphics(id);
        Expect(project, request.ExpectedSha256);
        var document = FactoryTalkGraphics.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision, project.Kind)));
        return GraphicsReferences.Extract(project, document, request);
    }
}
