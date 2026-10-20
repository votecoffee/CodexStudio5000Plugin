using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Studio5000.Client;
using Studio5000.Core;

namespace Studio5000.Mcp;

internal sealed class McpServiceErrorException(ServiceError error) : McpException(error.ToJson())
{
    public ServiceError Error { get; } = error;
}

internal static class ServiceErrors
{
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(McpRequestHandler<CallToolRequestParams, CallToolResult> next) => async (context, ct) =>
    {
        try { return await next(context, ct); }
        catch (McpServiceErrorException error)
        {
            // Only bounded HTTP domain envelopes enter this path. Unknown exceptions
            // retain the SDK's generic handling; state stays on this request alone.
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = error.Error.ToJson() }],
                StructuredContent = JsonSerializer.SerializeToElement(error.Error, ProjectService.Json)
            };
        }
    };
}
