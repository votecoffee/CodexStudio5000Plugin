using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Studio5000.Client;
using Studio5000.Core;

internal static class BrokerIntegration
{
    public static async Task<List<string>> RunAsync(ServiceClient client, McpClient mcp)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); Console.WriteLine("PASS: " + name); }
        async Task<JsonElement> Read(string tool, JsonElement arguments)
        {
            var result = await mcp.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments.GetRawText()));
            if (result.IsError == true) throw new Exception("Broker metadata read failed: " + tool);
            return JsonSerializer.Deserialize<JsonElement>(result.Content.OfType<TextContentBlock>().Single().Text);
        }
        var tools = await mcp.ListToolsAsync();
        foreach (var name in new[] { "rockwell_broker_capabilities", "rockwell_read_workflow", "rockwell_read_workflow_reference" })
            Check(tools.Single(t => t.Name == name).ProtocolTool.Annotations?.ReadOnlyHint == true, name + " explicitly declares read-only metadata");
        var catalog = await Read("rockwell_broker_capabilities", JsonSerializer.SerializeToElement(new { }));
        var rest = await client.GetAsync<JsonElement>("/api/broker/capabilities");
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(catalog.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(rest.GetRawText())), "REST and MCP publish the same normalized broker catalog");
        var capabilities = catalog.GetProperty("capabilities").EnumerateArray().ToArray();
        Check(capabilities.Count(c => c.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == "workflow")) == 3, "Broker route publishes all three standalone workflow guides");
        Check(capabilities.All(c => tools.Any(t => t.Name == c.GetProperty("discovery").GetProperty("tool").GetString() && t.ProtocolTool.Annotations?.ReadOnlyHint == true)), "All broker discovery references name actual read-only tools");
        foreach (var capability in capabilities)
        {
            var discovery = capability.GetProperty("discovery"); var tool = discovery.GetProperty("tool").GetString()!;
            if (tool != "rockwell_read_workflow") continue;
            var guide = await Read(tool, discovery.GetProperty("arguments"));
            var instructions = guide.GetProperty("instructions");
            Check(ProjectService.Hash(Encoding.UTF8.GetBytes(instructions.GetProperty("text").GetString()!)).ToLowerInvariant() == instructions.GetProperty("sha256").GetString(), "Broker workflow content matches its exact digest: " + guide.GetProperty("id").GetString());
            Check(guide.GetProperty("tools").EnumerateArray().All(t => tools.Any(native => native.Name == t.GetProperty("tool").GetString())), "Broker workflow bindings name actual MCP tools: " + guide.GetProperty("id").GetString());
            foreach (var reference in guide.GetProperty("references").EnumerateArray())
            {
                var reader = reference.GetProperty("read"); var result = await Read(reader.GetProperty("tool").GetString()!, reader.GetProperty("arguments"));
                Check(ProjectService.Hash(Encoding.UTF8.GetBytes(result.GetProperty("content").GetString()!)).ToLowerInvariant() == reference.GetProperty("sha256").GetString(), "Broker reference content matches the guide's pinned digest");
            }
        }
        var stale = await mcp.CallToolAsync("rockwell_read_workflow", new Dictionary<string, object?> { ["workflowId"] = "rockwell-engineering", ["expectedRevision"] = "stale" });
        Check(stale.IsError == true, "MCP rejects stale workflow guidance instead of substituting current instructions");
        return checks;
    }
}
