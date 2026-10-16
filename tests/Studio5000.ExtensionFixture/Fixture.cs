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
#if FIXTURE_V2
        operations.Add(new("fixture_added_in_v2", "New code and operation discovered without reconnecting MCP", "read", schema));
#endif
        return operations.ToArray();
    }
    public async Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, ExtensionContext context, CancellationToken ct)
    {
        if (arguments.TryGetProperty("delayMs", out var delay)) await Task.Delay(Math.Clamp(delay.GetInt32(), 0, 15000), ct);
        return JsonSerializer.SerializeToElement(new { version = Version, operation, processId = Environment.ProcessId });
    }
}
