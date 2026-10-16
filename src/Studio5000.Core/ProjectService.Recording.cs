using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Studio5000.Core;

public sealed partial class ProjectService
{
    public WatchRecordingStatus RecordingStatus(string id) { lock (watchLock) return Watch(id).Recorder.Status; }
    public WatchRecordingStatus ConfigureRecording(string id, WatchRecordingUpdate request)
    {
        lock (watchLock)
        {
            var watch = Watch(id); watch.Recorder.Configure(request, watch.Definition.Revision, watch.Session);
            return watch.Recorder.Status;
        }
    }
    public WatchRecordingPage QueryRecording(string id, WatchRecordingQuery request)
    {
        lock (watchLock)
        {
            var watch = Watch(id); var records = new List<WatchRecord>(); long bytes = 0; var more = false;
            foreach (var entry in watch.Recorder.Query(request, watch.Definition.Revision))
            {
                var size = JsonSerializer.SerializeToUtf8Bytes(entry, Json).Length;
                if (records.Count >= request.Limit || bytes + size > 8 * 1024 * 1024) { more = true; break; }
                records.Add(entry); bytes += size;
            }
            return new(records.ToArray(), records.LastOrDefault()?.Index ?? request.AfterIndex, more, DateTimeOffset.UtcNow,
                Status(watch).Stale, watch.State, watch.Recorder.Status);
        }
    }
    public Artifact ExportRecording(string id, WatchRecordingQuery request)
    {
        lock (watchLock)
        {
            var watch = Watch(id); var artifactId = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(artifactId); Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "project.csv");
            using (var output = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                output.WriteLine("index,watch_revision,session,event,started_utc,completed_utc,mode,key,program,tag,member,data_type,quality,value_json,detail");
                var definitions = new Dictionary<int, WatchDefinition>();
                foreach (var entry in watch.Recorder.Query(request, watch.Definition.Revision))
                {
                    if (!definitions.TryGetValue(entry.WatchRevision, out var definition)) definitions[entry.WatchRevision] = definition = watch.Recorder.Definition(entry.WatchRevision);
                    void Row(WatchValue? value)
                    {
                        var tag = definition.Settings.Tags.SingleOrDefault(t => t.Key == value?.Key);
                        var cells = new[] { entry.Index.ToString(CultureInfo.InvariantCulture), entry.WatchRevision.ToString(CultureInfo.InvariantCulture), entry.Session, entry.Kind,
                            (entry.Snapshot?.StartedAt ?? entry.At).ToUniversalTime().ToString("O"), entry.At.ToUniversalTime().ToString("O"), entry.Snapshot?.Mode ?? definition.Settings.Mode,
                            value?.Key, value?.ProgramName, value?.TagName, tag?.MemberPath, value?.DataType, value?.Quality, value?.Value?.ToJsonString(), value?.Error ?? entry.Detail };
                        output.WriteLine(string.Join(',', cells.Select(Csv)));
                    }
                    if (entry.Snapshot is null) Row(null); else foreach (var value in entry.Snapshot.Values) Row(value);
                }
            }
            var data = File.ReadAllBytes(path);
            var artifact = new Artifact(artifactId, id, "csv", Hash(data), data.Length, "Recorded reads with UTC timestamps, quality and explicit session/gap events. Offline samples are saved data, not controller observations.");
            Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
        }
    }
    private static string Csv(string? value)
    {
        value ??= "";
        // Quoting alone does not stop spreadsheet formula execution in text cells.
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
