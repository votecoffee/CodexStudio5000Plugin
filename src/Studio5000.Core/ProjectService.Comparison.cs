namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public LadderPreviewReport PreviewLadder(string id, LadderPreviewRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a registered Logix snapshot.");
        if (string.IsNullOrWhiteSpace(request.RoutineXPath) || request.RoutineXPath.Length > 8192)
            throw new OperationException("invalid_ladder_query", "Provide an exact routine selector up to 8192 characters.");
        var document = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision)));
        var routine = XmlProjects.Select(document, request.RoutineXPath);
        if (routine.Name != "Routine" || (string?)routine.Attribute("Type") != "RLL")
            throw new OperationException("not_ladder", "Select exactly one RLL routine.");
        return LadderPreview.Build(project, routine, request);
    }

    public ProjectComparisonReport CompareProjects(ProjectCompareRequest request)
    {
        var left = Get(request.LeftProjectId); var right = Get(request.RightProjectId);
        if (left.Kind != "logix" || right.Kind != "logix") throw new OperationException("not_logix", "Select two registered Logix snapshots.");
        Expect(left, request.LeftSha256); Expect(right, request.RightSha256);
        return ProjectComparison.Compare(left, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(left.Id, left.Revision))),
            right, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(right.Id, right.Revision))), request);
    }
}
