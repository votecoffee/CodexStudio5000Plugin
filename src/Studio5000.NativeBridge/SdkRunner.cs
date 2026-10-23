using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf;
using RockwellAutomation.LogixDesigner;
using RockwellAutomation.LogixDesigner.Logging;
using Studio5000.Core;
using Studio5000.Core.Resources;

namespace Studio5000.NativeBridge;

internal static partial class SdkRunner
{
    private static readonly JsonSerializerOptions Json = new(ProjectService.Json) { Converters = { new JsonStringEnumConverter() } };
    private static T Arg<T>(Dictionary<string, JsonElement> args, string name, T fallback = default!) =>
        args.TryGetValue(name, out var value) ? value.Deserialize<T>(Json)! : fallback;

    public static object Coverage()
    {
        var actual = typeof(LogixProject).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        static string Key(string name, IEnumerable<string> parameters) => name + "(" + string.Join(",", parameters) + ")";
        var expected = SdkCatalog.Surface.Methods.Select(m => Key(m.Name, m.Parameters.Select(p => p.Type))).Order().ToArray();
        var found = actual.Select(m => Key(m.Name, m.GetParameters().Select(p => p.ParameterType.ToString()))).Order().ToArray();
        var bound = BoundMethodNames();
        return new { assembly = typeof(LogixProject).Assembly.FullName, signatureCount = actual.Length,
            operationCount = SdkCatalog.Methods.Length, managedMethods = SdkCatalog.ManagedMethods,
            alternateLoggerOverloads = 4, matchesFrozenSurface = expected.SequenceEqual(found),
            missing = expected.Except(found), unexpected = found.Except(expected), compileBoundMethodNames = bound,
            allOperationsBound = SdkCatalog.Methods.All(m => bound.Contains(m.Name)),
            unboundOperationNames = SdkCatalog.Methods.Select(m => m.Name).Except(bound),
            loggerOverloadPolicy = "Single-handler overloads are bound; enumerable-handler overloads are adapted by the bridge's event collector. Arbitrary callback code is not accepted over HTTP." };
    }

    private static string[] BoundMethodNames()
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public).Where(f => f.FieldType == typeof(OpCode))
            .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
        var found = new HashSet<string>();
        foreach (var type in typeof(SdkRunner).Assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray(); if (il is null) continue;
            for (var i = 0; i < il.Length;)
            {
                ushort code = il[i++]; if (code == 0xfe) code = (ushort)(0xfe00 | il[i++]);
                var op = opcodes[code];
                if (op.OperandType == OperandType.InlineMethod)
                {
                    try { var target = method.Module.ResolveMethod(BitConverter.ToInt32(il, i)); if (target?.DeclaringType == typeof(LogixProject)) found.Add(target.Name); }
                    catch (ArgumentException) { } // unrelated generic metadata tokens do not affect SDK calls
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4
                };
            }
        }
        return found.Order().ToArray();
    }

    public static async Task<int> RunAsync(string resultPath)
    {
        var request = JsonSerializer.Deserialize<SdkBridgeRequest>(await Console.In.ReadToEndAsync(), Json)
            ?? throw new ArgumentException("Missing request.");
        SdkCatalog.ValidateCalls(request.Calls);
        NativeExecutionPolicy.ValidateWorkerProfile(request.ExecutionProfile, Environment.GetEnvironmentVariable(NativeExecutionPolicy.EnvironmentKey));
        NativeExecutionPolicy.ValidateCalls(request.ExecutionProfile, request.Calls, request.CommunicationsPath);
        using var lifecycle = NativeLifecycleJournal.FromEnvironment();
        var secrets = request.Calls.SelectMany(c => c.Arguments).Where(p => SdkCatalog.IsSecret(p.Key))
            .Select(p => p.Value.GetString()).Where(s => !string.IsNullOrEmpty(s)).Cast<string>().Distinct().OrderByDescending(s => s.Length).ToArray();
        string Redact(string text) { foreach (var secret in secrets) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal); return text; }
        var events = new List<SdkEvent>(); var steps = new List<SdkStepResult>();
        var logger = new CollectEvents(events, Redact);
        LogixProject? project = null;
        SdkBridgeResult result;
        // Vendor code may write to Console. Keep it out of the structured result and never persist raw output.
        Console.SetOut(TextWriter.Null);
        try
        {
            if (request.SourcePath is not null && !SdkCatalog.Factories.Contains(request.Calls[0].Method))
                project = await LogixProject.OpenLogixProjectAsync(request.SourcePath, logger);
            if (project is not null) await RequireLocalOfflineAsync(project, request.ExecutionProfile);
            if (project is not null && request.CommunicationsPath is not null)
                await project.SetCommunicationsPathAsync(request.CommunicationsPath);
            for (var i = 0; i < request.Calls.Length; i++)
            {
                var call = request.Calls[i];
                object? value;
                try
                {
                    if (SdkCatalog.Factories.Contains(call.Method))
                    {
                        lifecycle?.BeforeStep(i, call.Method);
                        project = await CreateAsync(call.Method, call.Arguments, logger, CancellationToken.None);
                        await RequireLocalOfflineAsync(project, request.ExecutionProfile);
                        if (request.CommunicationsPath is not null) await project.SetCommunicationsPathAsync(request.CommunicationsPath);
                        value = new { opened = true };
                    }
                    else
                    {
                        if (!SdkCatalog.Find(call.Method).IsStatic && project is null) throw new ArgumentException("This method requires a source project or a preceding project factory.");
                        lifecycle?.BeforeStep(i, call.Method);
                        value = await InvokeAsync(project, call.Method, call.Arguments, CancellationToken.None);
                    }
                    JsonElement? json = value switch
                    {
                        null => null,
                        ByteString bytes => JsonSerializer.SerializeToElement(new { base64 = bytes.ToBase64(), bytes = bytes.Length }, Json),
                        IMessage message => JsonDocument.Parse(JsonFormatter.Default.Format(message)).RootElement.Clone(),
                        _ => JsonSerializer.SerializeToElement(value, Json)
                    };
                    steps.Add(new(i, call.Method, "succeeded", json));
                    lifecycle?.AfterStep();
                }
                catch { steps.Add(new(i, call.Method, "failed", null)); throw; }
            }
            if (project is not null)
            {
                lifecycle?.BeforeStep(request.Calls.Length, "PreserveWorkingCopyAndDisconnect");
                await RequireLocalOfflineAsync(project, request.ExecutionProfile);
                if (await project.ReadConnectedStateAsync() is LogixProject.ConnectedState.Online or LogixProject.ConnectedState.Connected)
                    await project.GoOfflineAsync();
                await project.SaveAsAsync(Path.Combine(request.WorkDirectory, "final.acd"));
                await project.SaveAsAsync(Path.Combine(request.WorkDirectory, "final.l5x"));
                lifecycle?.AfterStep();
            }
            result = new("succeeded", steps.ToArray(), events.ToArray());
        }
        catch (Exception ex)
        {
            result = new("failed", steps.ToArray(), events.ToArray(), Redact(ex.Message));
            // Save only a separate private recovery copy. This preserves dirty
            // work without clearing the failed call's unknown external effects.
            if (project is not null)
            {
                try
                {
                    await RequireLocalOfflineAsync(project, request.ExecutionProfile);
                    if (await project.ReadConnectedStateAsync() is LogixProject.ConnectedState.Online or LogixProject.ConnectedState.Connected) await project.GoOfflineAsync();
                    await project.SaveAsAsync(Path.Combine(request.WorkDirectory, "recovery.acd"), false);
                }
                catch { /* Retained lifecycle barrier and SDK failure remain authoritative. */ }
            }
        }
        finally
        {
            if (project is not null)
            {
                // These callbacks are lifecycle plumbing, not caller-supplied executable handlers.
                project.AddEventHandler(logger); project.RemoveEventHandler(logger); project.ClearEventHandlers();
                project.Dispose();
            }
        }
        lifecycle?.Complete(result.Status == "succeeded", result.Status == "succeeded", true);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, Json));
        return 0; // Domain failures are represented by the result, not a lost process response.
    }

    private static async Task RequireLocalOfflineAsync(LogixProject project, string profile)
    {
        if (profile == NativeExecutionPolicy.LocalProject)
            NativeExecutionPolicy.ValidateProjectConnection(profile, (await project.ReadConnectedStateAsync()).ToString());
    }
    private sealed class CollectEvents(List<SdkEvent> events, Func<string, string> redact) : IOperationEvent
    {
        private bool progress = true, status = true, error = true;
        public void LogProgress(bool enabled) => progress = enabled;
        public void LogStatus(bool enabled) => status = enabled;
        public void LogError(bool enabled) => error = enabled;
        private void Add(string kind, string message, int? value = null)
        { lock (events) { if (events.Count < 5000) events.Add(new(kind, redact(message), value)); } }
        public void Progress(string projectFile, int value) { if (progress) Add("progress", "SDK progress", value); }
        public void Status(string projectFile, string message) { if (status) Add("status", message); }
        public void Error(string projectFile, string message) { if (error) Add("error", message); }
    }
}
