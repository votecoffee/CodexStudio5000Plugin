using System.Text.Json;
using System.Text.Json.Nodes;
using libplctag;
using Studio5000.Core;

// Deliberately contains no tag writes, arbitrary CIP messages, discovery, or autonomous polling.
var wire = Console.Out; Console.SetOut(Console.Error);
var json = new JsonSerializerOptions(ProjectService.Json) { WriteIndented = false };
if (args is ["--self-test", var fixture])
{
    var plan = CipWatchPlanning.Build(XmlProjects.Parse(await File.ReadAllBytesAsync(fixture)), "fixture",
        [new("record", "Record", "WatchProgram"), new("count", "Record", "WatchProgram", "Inner.Count")]);
    var reads = 0;
    var result = await CipWatchPlanning.ReadAsync(plan, (node, _) =>
    {
        reads++; CipWatchPlanning.ValidateType(node, [(byte)node.CipType!, 0]);
        return Task.FromResult<JsonNode?>(JsonValue.Create(node.DataType == "BOOL" ? (object)true : 42));
    }, default);
    if (reads != plan.UniqueLeafReads || result.Length != 2 || result[1].Value!.GetValue<int>() != 42) throw new InvalidOperationException("Generated read self-test failed.");
    await wire.WriteLineAsync(JsonSerializer.Serialize(new { success = true, leafReads = reads,
        nativeVersion = $"{LibPlcTag.VersionMajor}.{LibPlcTag.VersionMinor}.{LibPlcTag.VersionPatch}", controllerConnected = false }, json));
    return;
}
var handles = new Dictionary<string, Tag>(StringComparer.Ordinal);
try
{
    var input = await Console.In.ReadLineAsync() ?? throw new IOException("Missing startup request.");
    var request = JsonSerializer.Deserialize<WatchBridgeOpen>(input, json) ?? throw new IOException("Empty startup request.");
    if (request is not { Backend: "LibPlcTag", Mode: "Online", CipTarget: not null, AllowedTags: not null } ||
        !Path.GetExtension(request.SourcePath).Equals(".l5x", StringComparison.OrdinalIgnoreCase))
        throw new OperationException("cip_request", "Expected an online read-only L5X watch session.");
    CipWatchPlanning.ValidateTarget(request.CipTarget);
    var bytes = await File.ReadAllBytesAsync(request.SourcePath);
    // Validate every selection before creating a libplctag handle (InitializeAsync is the first network operation).
    var plan = CipWatchPlanning.Build(XmlProjects.Parse(bytes), ProjectService.Hash(bytes), request.AllowedTags);
    await File.WriteAllBytesAsync(Path.Combine(request.WorkDirectory, "metadata.l5x"), bytes);
    await Reply(new(true, []));
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var command = JsonSerializer.Deserialize<WatchBridgeCommand>(line, json) ?? throw new IOException("Empty read command.");
        if (command.Tags is null || !command.Tags.SequenceEqual(request.AllowedTags)) throw new OperationException("cip_scope", "Selections changed; restart with a newly authorized list.");
        var values = await CipWatchPlanning.ReadAsync(plan, ReadLeaf, default);
        await Reply(new(true, [], SelectedValues: values));
    }
    async Task<JsonNode?> ReadLeaf(CipReadNode node, CancellationToken ct)
    {
        if (!handles.TryGetValue(node.Address!, out var tag))
        {
            tag = new Tag { Name = node.Address!, Gateway = request.CipTarget.Gateway, Path = request.CipTarget.Route,
                PlcType = PlcType.ControlLogix, Protocol = Protocol.ab_eip, ElementCount = 1,
                Timeout = TimeSpan.FromMilliseconds(request.CipTarget.TimeoutMs), ReadCacheMillisecondDuration = 0,
                AutoSyncReadInterval = null, AutoSyncWriteInterval = null };
            handles.Add(node.Address!, tag); await tag.InitializeAsync(ct);
        }
        await tag.ReadAsync(ct);
        CipWatchPlanning.ValidateType(node, tag.GetByteArrayAttribute("raw_tag_type_bytes"));
        return node.DataType switch
        {
            "BOOL" => JsonValue.Create(tag.GetBit(0)), "SINT" => JsonValue.Create(tag.GetInt8(0)),
            "INT" => JsonValue.Create(tag.GetInt16(0)), "DINT" => JsonValue.Create(tag.GetInt32(0)), "LINT" => JsonValue.Create(tag.GetInt64(0)),
            "USINT" => JsonValue.Create(tag.GetUInt8(0)), "UINT" => JsonValue.Create(tag.GetUInt16(0)),
            "UDINT" => JsonValue.Create(tag.GetUInt32(0)), "ULINT" => JsonValue.Create(tag.GetUInt64(0)),
            "REAL" => Finite(tag.GetFloat32(0)), "LREAL" => Finite(tag.GetFloat64(0)),
            _ => throw new OperationException("cip_type_unsupported", "Unsupported scalar type.")
        };
    }
}
catch (Exception ex) { await Reply(new(false, [], ex.Message)); Environment.ExitCode = 1; }
finally { foreach (var tag in handles.Values) tag.Dispose(); }
Task Reply(WatchBridgeReply reply) => wire.WriteLineAsync(JsonSerializer.Serialize(reply, json));
static JsonNode Finite(double number) => double.IsFinite(number) ? JsonValue.Create(number) : throw new OperationException("cip_value", "Nonfinite floating-point values cannot be recorded as JSON numbers.");
