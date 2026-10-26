using Studio5000.Core;
using Studio5000.Mcp;

if (args is ["--version"]) { Console.WriteLine(WorkspaceIdentity.Version); return; }
await McpApplication.RunAsync(args, McpFrontdoorPolicy.Read(AppContext.BaseDirectory));
