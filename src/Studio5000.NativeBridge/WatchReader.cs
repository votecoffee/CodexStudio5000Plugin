using System.Text.Json;
using RockwellAutomation.LogixDesigner;
using Studio5000.Core;

namespace Studio5000.NativeBridge;

internal static partial class SdkRunner
{
    // One read-only controller session plus a separate OFFLINE scratch project for SDK-native decoding.
    // EOF when the parent service exits closes both sessions; no saves or writes target the controller.
    public static async Task<int> RunWatchAsync()
    {
        var protocol = Console.Out; Console.SetOut(TextWriter.Null);
        var json = new JsonSerializerOptions(ProjectService.Json) { WriteIndented = false };
        LogixProject? source = null, mirror = null;
        async Task Reply(WatchBridgeReply reply) { await protocol.WriteLineAsync(JsonSerializer.Serialize(reply, json)); await protocol.FlushAsync(); }
        try
        {
            var line = await Console.In.ReadLineAsync() ?? throw new IOException("No watch startup request.");
            var request = JsonSerializer.Deserialize<WatchBridgeOpen>(line, json)!;
            if (request.Mode is not ("Online" or "Offline")) throw new ArgumentException("Invalid watch mode.");
            var mirrorPath = Path.Combine(request.WorkDirectory, "mirror" + Path.GetExtension(request.SourcePath));
            File.Copy(request.SourcePath, mirrorPath, false);
            source = await LogixProject.OpenLogixProjectAsync(request.SourcePath);
            mirror = await LogixProject.OpenLogixProjectAsync(mirrorPath);
            await mirror.SaveAsAsync(Path.Combine(request.WorkDirectory, "metadata.l5x"));
            if (request.Mode == "Online")
            {
                if (string.IsNullOrWhiteSpace(request.CommunicationsPath)) throw new ArgumentException("Missing configured communications path.");
                await source.SetCommunicationsPathAsync(request.CommunicationsPath);
                await source.GoOnlineAsync();
                if (await source.ReadConnectedStateAsync() != LogixProject.ConnectedState.Online) throw new IOException("Watch source is not online.");
            }
            var mode = request.Mode == "Online" ? LogixProject.OperationMode.Online : LogixProject.OperationMode.Offline;
            await Reply(new(true, []));
            while ((line = await Console.In.ReadLineAsync()) is not null)
            {
                var command = JsonSerializer.Deserialize<WatchBridgeCommand>(line, json)!;
                if (command.Tags is null || command.Tags.Length is < 1 or > 64) throw new ArgumentException("Invalid tag batch.");
                var values = new List<StructuredTag>();
                foreach (var tag in command.Tags.DistinctBy(t => (t.ProgramName, t.TagName)))
                {
                    var path = WatchValidation.TagPath(tag);
                    try
                    {
                        var raw = await source.GetTagValueAsync(path, mode, LogixProject.DataType.BYTE_ARRAY);
                        if (raw.Length > 30000) throw new IOException("Tag value exceeds the SDK's 30000-byte operation limit.");
                        // This is a different offline project, never the connected source. Its XML is the layout authority.
                        await mirror.SetTagValueAsync(path, LogixProject.OperationMode.Offline, raw.ToByteArray(), LogixProject.DataType.BYTE_ARRAY);
                        var export = Path.Combine(request.WorkDirectory, "value.l5x");
                        if (File.Exists(export)) File.Delete(export);
                        await mirror.PartialExportToXmlFileAsync(path, export);
                        var doc = XmlProjects.Parse(await File.ReadAllBytesAsync(export));
                        var result = LogixStructures.ExtractTag(LogixStructures.Tag(doc, tag.TagName, tag.ProgramName), tag.ProgramName);
                        values.Add(result with { Quality = result.Quality == "snapshot" ? "good" : result.Quality });
                    }
                    catch (Exception ex) { values.Add(new(tag.TagName, tag.ProgramName, "Unknown", [], null, "bad", ex.Message)); }
                }
                await Reply(new(true, values.ToArray()));
            }
            return 0;
        }
        catch (Exception ex) { await Reply(new(false, [], ex.Message)); return 1; }
        finally
        {
            if (source is not null)
            {
                try { if (await source.ReadConnectedStateAsync() is LogixProject.ConnectedState.Online or LogixProject.ConnectedState.Connected) await source.GoOfflineAsync(); }
                finally { source.Dispose(); }
            }
            mirror?.Dispose();
        }
    }
}
