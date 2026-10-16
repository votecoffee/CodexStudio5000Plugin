using System.Text;
using System.Text.Json;

namespace Studio5000.Core;

public sealed record WatchRecordingSettings(bool Enabled = true, int RetentionDays = 7, int MaxMiB = 64);
public sealed record WatchRecordingUpdate(WatchRecordingSettings Settings, int ExpectedRevision);
public sealed record WatchRecordingStatus(WatchRecordingSettings Settings, int Revision, long Bytes, long LastIndex, string? RecoveryNote);
public sealed record WatchRecord(long Index, int WatchRevision, string Session, DateTimeOffset At, string Kind, WatchSnapshot? Snapshot = null, string? Detail = null);
public sealed record WatchRecordingQuery(int? WatchRevision = null, DateTimeOffset? From = null, DateTimeOffset? To = null, long AfterIndex = 0, int Limit = 1000);
public sealed record WatchRecordingPage(WatchRecord[] Records, long NextIndex, bool HasMore, DateTimeOffset ServerTime, bool Stale, string State, WatchRecordingStatus Recording);

// A bounded, segmented journal. Callers serialize access using the shared watch lock.
internal sealed class WatchRecorder
{
    private const long SegmentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Compact = new(ProjectService.Json) { WriteIndented = false };
    private readonly string directory;
    private long lastIndex;
    private int revision;
    private WatchRecordingSettings settings = new();
    private string? recoveryNote;
    private string SettingsPath => Path.Combine(directory, "settings.json");
    public WatchRecordingStatus Status { get { Retain(); return new(settings, revision, Segments().Sum(f => new FileInfo(f).Length), lastIndex, recoveryNote); } }
    private string[] Segments() => Directory.GetFiles(directory, "segment-*.jsonl").Order(StringComparer.Ordinal).ToArray();

    public WatchRecorder(string watchDirectory, WatchDefinition definition)
    {
        directory = Path.Combine(watchDirectory, "recording"); Directory.CreateDirectory(directory);
        if (File.Exists(SettingsPath))
        {
            var config = JsonSerializer.Deserialize<WatchRecordingUpdate>(File.ReadAllBytes(SettingsPath), Compact)!;
            settings = config.Settings; revision = config.ExpectedRevision; Validate(settings);
        }
        var files = Segments();
        // Only a torn final append is recoverable. Complete malformed records remain an error.
        if (files.Length > 0)
        {
            using var file = new FileStream(files[^1], FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (file.Length > 0)
            {
                file.Position = file.Length - 1;
                if (file.ReadByte() != '\n')
                {
                    var position = file.Length - 1;
                    while (position >= 0) { file.Position = position; if (file.ReadByte() == '\n') break; position--; }
                    file.SetLength(position + 1); file.Flush(true);
                    recoveryNote = "Recovered an incomplete final journal append; earlier complete records were retained.";
                }
            }
        }
        foreach (var record in Read())
        {
            if (record.Index <= lastIndex) throw new OperationException("recording_corrupt", "Recording indexes are out of order.");
            lastIndex = record.Index;
        }
        var cursor = Path.Combine(directory, "cursor.json");
        if (File.Exists(cursor)) lastIndex = Math.Max(lastIndex, JsonSerializer.Deserialize<long>(File.ReadAllBytes(cursor)));
        PreserveDefinition(definition); Retain();
    }
    public void PreserveDefinition(WatchDefinition definition)
    {
        var path = Path.Combine(directory, $"revision-{definition.Revision}.json");
        if (!File.Exists(path)) WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(definition, Compact));
    }
    public WatchDefinition Definition(int watchRevision) => JsonSerializer.Deserialize<WatchDefinition>(
        File.ReadAllBytes(Path.Combine(directory, $"revision-{watchRevision}.json")), Compact)!;
    private static void Validate(WatchRecordingSettings value)
    {
        if (value.RetentionDays is < 1 or > 365 || value.MaxMiB is < 8 or > 256)
            throw new OperationException("recording_settings", "Use retention 1–365 days and storage 8–256 MiB per list.");
    }
    public void Configure(WatchRecordingUpdate request, int watchRevision, string session)
    {
        Validate(request.Settings);
        if (request.ExpectedRevision != revision) throw new OperationException("stale_revision", "Recording settings changed; refresh them.");
        WriteAtomic(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(new WatchRecordingUpdate(request.Settings, revision + 1), Compact));
        settings = request.Settings; revision++;
        Append(watchRevision, session, settings.Enabled ? "recording_enabled" : "recording_disabled", force: true);
        Retain();
    }
    public void Append(int watchRevision, string session, string kind, WatchSnapshot? sample = null, string? detail = null, bool force = false)
    {
        if (!settings.Enabled && !force) return;
        var record = new WatchRecord(lastIndex + 1, watchRevision, session, sample?.CompletedAt ?? DateTimeOffset.UtcNow, kind, sample, detail);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, Compact);
        var files = Segments(); var path = files.LastOrDefault();
        if (path is null || new FileInfo(path).Length + bytes.Length + 1 > SegmentBytes)
            path = Path.Combine(directory, $"segment-{record.Index:D20}.jsonl");
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        { file.Write(bytes); file.WriteByte((byte)'\n'); file.Flush(true); }
        lastIndex = record.Index; Retain();
        WriteAtomic(Path.Combine(directory, "cursor.json"), JsonSerializer.SerializeToUtf8Bytes(lastIndex));
    }
    public IEnumerable<WatchRecord> Read()
    {
        foreach (var path in Segments())
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                WatchRecord? record;
                try { record = JsonSerializer.Deserialize<WatchRecord>(line, Compact); }
                catch (JsonException ex) { throw new OperationException("recording_corrupt", $"Malformed complete journal entry: {ex.Message}"); }
                yield return record ?? throw new OperationException("recording_corrupt", "Empty recording entry.");
            }
    }
    public IEnumerable<WatchRecord> Query(WatchRecordingQuery request, int currentRevision)
    {
        if (request.Limit is < 1 or > 5000 || request.AfterIndex < 0 || request.WatchRevision < 0 || request.From > request.To)
            throw new OperationException("recording_query", "Use limit 1–5000, nonnegative cursor/revision and an ordered time range.");
        Retain();
        return Read().Where(r => r.WatchRevision == (request.WatchRevision ?? currentRevision) && r.Index > request.AfterIndex &&
            (request.From is null || r.At >= request.From) && (request.To is null || r.At <= request.To));
    }
    private void Retain()
    {
        var files = Segments(); var total = files.Sum(f => new FileInfo(f).Length);
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (total <= settings.MaxMiB * 1024L * 1024 && info.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-settings.RetentionDays)) break;
            total -= info.Length; File.Delete(file);
        }
    }
    private static void WriteAtomic(string path, byte[] bytes)
    { var tmp = path + ".tmp"; File.WriteAllBytes(tmp, bytes); File.Move(tmp, path, true); }
}
