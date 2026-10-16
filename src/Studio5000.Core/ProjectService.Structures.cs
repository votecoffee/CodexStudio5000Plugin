using System.Xml.Linq;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public StructureExtraction ExtractStructures(StructureExtractRequest request)
    {
        if ((request.ProjectId is null) == (request.JobId is null))
            throw new OperationException("structure_source", "Choose one managed projectId or completed SDK jobId.");
        SdkJob? job = null;
        if (request.JobId is not null)
        {
            job = SdkJob(request.JobId);
            if (job.Status != "succeeded" || job.ResultProject is null)
                throw new OperationException("structure_job", "Wait for a successful SDK job with a published result project.");
        }
        var info = Get(request.ProjectId ?? job!.ResultProject!.Id);
        if (info.Kind != "logix") throw new OperationException("sdk_project_kind", "Structures require a Logix project.");
        if (job is null && info.SourcePath.StartsWith("sdk-job:", StringComparison.Ordinal)) job = SdkJob(info.SourcePath[8..]);
        var doc = XmlProjects.Parse(File.ReadAllBytes(RevisionFile(info.Id, info.Revision)));
        var types = LogixStructures.Types(doc);
        if (request.DataTypeName is not null) types = types.Where(t => t.Name == request.DataTypeName).ToArray();
        var tags = doc.Root!.Element("Controller")!.Descendants("Tag")
            .Where(t => t.Parent?.Name == "Tags" && (t.Parent.Parent?.Name == "Controller" || t.Parent.Parent?.Name == "Program"))
            .Where(t => request.TagName is null || (string?)t.Attribute("Name") == request.TagName)
            .Where(t => request.ProgramName is null || (string?)t.Ancestors("Program").FirstOrDefault()?.Attribute("Name") == request.ProgramName)
            .Where(t => request.DataTypeName is null || (string?)t.Attribute("DataType") == request.DataTypeName).Take(257).ToArray();
        if (tags.Length > 256) throw new OperationException("structure_too_large", "Select a tag, program or data type (maximum 256 tags per extraction).");
        var uploaded = job?.Status == "succeeded" && job.Methods.Contains("UploadToNewProjectAsync") && info.Revision == 0;
        return new(info.Id, info.CurrentSha256, uploaded ? "controller_upload_snapshot" : "saved_project", uploaded ? job!.CreatedAt : null,
            types, tags.Select(t => LogixStructures.ExtractTag(t, (string?)t.Ancestors("Program").FirstOrDefault()?.Attribute("Name"), request.IncludeValues)).ToArray(),
            "Type declarations and decorated values extracted from a hash-checked L5X revision. Uploaded values are a point-in-time snapshot; watch samples carry separate read timestamps. No inferred memory offsets or atomic multi-tag snapshot is promised.");
    }

    public Task<SdkJob> PullStructuresAsync(StructurePullRequest request, CancellationToken ct = default) =>
        StartSdkJobAsync(new([new("UploadToNewProjectAsync", [])], ControllerTargetId: request.ControllerTargetId), ct);
}
