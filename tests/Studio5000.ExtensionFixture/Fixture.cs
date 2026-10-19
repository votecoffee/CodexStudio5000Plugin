using System.Text.Json;
using Studio5000.Extensibility;

namespace Studio5000.ExtensionFixture;

public sealed class Fixture : IWorkspaceExtension
{
#if FIXTURE_V2
    private const string Version = "2";
#else
    private const string Version = "1";
#endif
    public ExtensionOperation[] Describe()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { delayMs = new { type = "integer" } }, additionalProperties = false });
        var operations = new List<ExtensionOperation> { new("fixture_echo", "Offline extension reload fixture", "read", schema) };
        operations.Add(new("fixture_concurrency_barrier", "Generated local-only interprocess concurrency probe", "apply", JsonSerializer.SerializeToElement(new { type = "object", properties = new { barrierId = new { type = "string" } }, required = new[] { "barrierId" }, additionalProperties = false })));
#if FIXTURE_V2
        operations.Add(new("fixture_added_in_v2", "New code and operation discovered without reconnecting MCP", "read", schema));
        operations.Add(new("fixture_large_result", "Large recording response transport fixture", "read", schema));
#endif
        return operations.ToArray();
    }
    public async Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, ExtensionContext context, CancellationToken ct)
    {
        if (operation == "fixture_concurrency_barrier")
        {
            if (!Guid.TryParseExact(arguments.GetProperty("barrierId").GetString(), "N", out var barrier)) throw new ArgumentException("Expected fixture barrier ID.");
            var directory = Path.Combine(context.WorkspaceRoot, ".local", "concurrency-probes", barrier.ToString("N")); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, Environment.ProcessId + ".ready"), "ready", ct);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Directory.GetFiles(directory, "*.ready").Length < 2 && DateTime.UtcNow < deadline) await Task.Delay(10, ct);
            if (Directory.GetFiles(directory, "*.ready").Length < 2) throw new InvalidOperationException("Concurrent fixture handlers did not overlap.");
            return JsonSerializer.SerializeToElement(new { processId = Environment.ProcessId, overlapped = true });
        }
        if (arguments.TryGetProperty("delayMs", out var delay)) await Task.Delay(Math.Clamp(delay.GetInt32(), 0, 15000), ct);
        if (operation == "fixture_large_result") return JsonSerializer.SerializeToElement(new { payload = new string('x', 3 * 1024 * 1024) });
        return JsonSerializer.SerializeToElement(new { version = Version, operation, processId = Environment.ProcessId });
    }
}
