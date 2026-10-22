using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Studio5000.Core.Resources;

// Harmless test executable only: JSON/files/locks, no engineering SDK or host.
if (args.Length != 2) throw new ArgumentException("Use fixtures, roundtrip, admit or lock with an explicit disposable fixture directory.");
var root = Path.GetFullPath(args[1]); var marker = Path.Combine(root, ".resource-interop-fixture");
if (args[0] == "fixtures")
{
    if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("Use a new empty fixture directory.");
    Directory.CreateDirectory(root); File.WriteAllText(marker, "Harmless JSON/resource contract fixtures only");
    using var output = new StreamWriter(Path.Combine(root, "native.jsonl"), false, new UTF8Encoding(false));
    var values = new List<double> { 0.0, -0.0, 1, -1, 1e-5, 1e-4, 1e15, 1e16, double.Epsilon, double.MaxValue, -double.MaxValue, 1792690000.125 };
    var random = new Random(341); var bytes = new byte[8];
    for (var i = 0; i < 1000; i++) { random.NextBytes(bytes); var value = BitConverter.ToDouble(bytes); if (double.IsFinite(value)) values.Add(value); }
    for (var i = 0; i < values.Count; i++)
    {
        var record = Template(i, values[i]); var data = ResourceJson.Write(record);
        output.WriteLine(JsonSerializer.Serialize(new { record = JsonDocument.Parse(data).RootElement.Clone(), revision = record.Revision }));
    }
    var ledger = new ResourceLedger(Path.Combine(root, "registry"), new FakeDriver()); var owner = ledger.Owner("rockwell-interop");
    var intent = owner.BeginLaunch("Synthetic native record 🔧", "sdk-session", "shared"); owner.ActivateOpaqueAttachment(intent.ResourceId);
    Console.WriteLine(JsonSerializer.Serialize(new { fixtures = values.Count, resources = ledger.Records().Length, actualBusinessProcessesStarted = 0 }));
    return;
}
if (!File.Exists(marker)) throw new ArgumentException("An explicit interop fixture marker is required; never use a live registry.");
if (args[0].StartsWith("launch-", StringComparison.Ordinal))
{
    var python = File.ReadAllText(Path.Combine(root, "python-executable.txt")).Trim();
    if (Path.GetFileName(python) is not ("python.exe" or "pythonw.exe")) throw new ArgumentException("Use only the harmless Python fixture executable.");
    var driver = new WindowsResourceProcessDriver(); var ledger = new ResourceLedger(Path.Combine(root, "native-launch-registry"), driver);
    var owner = ledger.Owner("rockwell-launch-fixture");
    var executed = Path.Combine(root, "executed");
    var code = args[0] == "launch-echo"
        ? "import sys;from pathlib import Path;Path(sys.argv[1]).write_text('executed');data=sys.stdin.buffer.read();sys.stdout.buffer.write(data);sys.stderr.write('fixture-error')"
        : "import subprocess,sys,time;from pathlib import Path;p=subprocess.Popen([sys.executable,'-c','import time;time.sleep(60)']);Path(sys.argv[1]).write_text(str(p.pid));print('ready',flush=True);time.sleep(60)";
    var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add("-c"); start.ArgumentList.Add(code); start.ArgumentList.Add(executed);
    if (args[0] == "launch-fail")
    {
        try
        {
            using var unexpected = WindowsChildProcess.Launch(owner, start, "Harmless failed-start fixture", maximumProcesses: 8,
                beforeResume: record => { if (File.Exists(executed) || ledger.Get(record.ResourceId).State != "active") throw new InvalidOperationException("Identity was not bound before execution"); throw new IOException("Synthetic pre-resume refusal"); });
            throw new InvalidOperationException("Failure injection was ignored");
        }
        catch (IOException ex) when (ex.Message == "Synthetic pre-resume refusal") { }
        var failed = ledger.Records().Single();
        if (failed.State != "failed" || File.Exists(executed)) throw new InvalidOperationException("Failed startup executed fixture code");
        File.WriteAllText(Path.Combine(root, "launch-result.json"), JsonSerializer.Serialize(new { failed = true, failed.ResourceId }));
        return;
    }
    using var child = WindowsChildProcess.Launch(owner, start, "Harmless redirected process fixture", maximumProcesses: 8,
        beforeResume: record => { if (File.Exists(executed) || ledger.Get(record.ResourceId).State != "active") throw new InvalidOperationException("Fixture executed before durable identity binding"); });
    if (args[0] == "launch-echo")
    {
        using var output = new MemoryStream(); var stdout = child.StandardOutput.BaseStream.CopyToAsync(output);
        var stderr = child.StandardError.ReadToEndAsync();
        await child.StandardInput.BaseStream.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(root, "input.bin"))); child.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await child.WaitForExitAsync(deadline.Token); await stdout;
        await File.WriteAllBytesAsync(Path.Combine(root, "output.bin"), output.ToArray());
        if (await stderr != "fixture-error" || child.ExitCode != 0) throw new InvalidOperationException("Redirected fixture output/exit mismatch");
    }
    else
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (await child.StandardOutput.ReadLineAsync(deadline.Token) != "ready") throw new InvalidOperationException("Harmless child did not become ready");
        var observed = child.Refresh();
        File.WriteAllBytes(Path.Combine(root, "launch-active.json"), ResourceJson.Write(observed));
        if (args[0] == "launch-hold")
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(root, "hold-release"))) { if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(); Thread.Sleep(10); }
        }
        if (args[0] == "launch-refuse")
        {
            child.SetRecoveryBarrier("Synthetic dirty-state refusal");
            try { child.Close(); throw new InvalidOperationException("Recovery barrier was ignored"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("blocks cleanup", StringComparison.Ordinal)) { }
            if (child.HasExited) throw new InvalidOperationException("Refusal killed the owned process");
            var pending = child.Record;
            child.ResolveRecoveryBarrier(pending.Revision, "Only the harmless fixture state was inspected; no native work exists");
        }
    }
    var receipt = child.Close();
    File.WriteAllText(Path.Combine(root, "launch-result.json"), JsonSerializer.Serialize(new { receipt.Outcome, child.ResourceId }));
    return;
}
if (args[0] == "identity")
{
    var driver = new WindowsResourceProcessDriver();
    File.WriteAllText(Path.Combine(root, "identity.json"), JsonSerializer.Serialize(new { identity = driver.CurrentIdentity(), registry = WindowsResourceProcessDriver.DefaultRegistry() }));
    var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
    while (!File.Exists(Path.Combine(root, "identity-release"))) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(); Thread.Sleep(10); }
    return;
}
if (args[0] == "roundtrip")
{
    using var output = new StreamWriter(Path.Combine(root, "roundtrip.jsonl"), false, new UTF8Encoding(false));
    foreach (var line in File.ReadLines(Path.Combine(root, "python.jsonl")))
    {
        var record = ResourceJson.Read(Encoding.UTF8.GetBytes(line));
        output.WriteLine(JsonSerializer.Serialize(new { record = JsonDocument.Parse(ResourceJson.Write(record)).RootElement.Clone(), revision = record.Revision }));
    }
    return;
}
if (args[0] == "admit")
{
    try
    {
        var ledger = new ResourceLedger(Path.Combine(root, "registry"), new FakeDriver());
        var owner = ledger.Owner("rockwell-interop"); var intent = owner.BeginLaunch("Interop admission");
        Console.WriteLine(intent.ResourceId); return;
    }
    catch (IOException) { Environment.ExitCode = 23; return; }
}
if (args[0] == "lock")
{
    var path = Path.Combine(root, "registry", "resources", "ledger.lock");
    using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
    if (file.Length == 0) { file.WriteByte(48); file.Flush(true); }
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    file.Lock(0, 1);
    try
    {
        File.WriteAllText(Path.Combine(root, "lock-ready"), "ready");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!File.Exists(Path.Combine(root, "lock-release"))) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(); Thread.Sleep(10); }
    }
    finally { file.Unlock(0, 1); }
    return;
}
throw new ArgumentException("Unknown harmless fixture command.");

static ResourceRecord Template(int index, double timestamp)
{
    var identity = new ProcessIdentity(new('a', 64), 42, "100", @"C:\fixture\München\host.exe").Normalize();
    return new ResourceRecord { ResourceId = (index + 1).ToString("x32"), AppId = "rockwell-interop", OwnerId = new('c', 32),
        Owner = identity, Label = "Generated \"München\" 🔧\n\\<&>", CreatedAt = timestamp, UpdatedAt = timestamp,
        Receipt = index % 3 == 0 ? new(new('d', 32), new('e', 64), "uncertain", "Inspect retained evidence") : null,
        RecoveryBarrier = index % 3 == 0 ? "Uncertain native fixture" : null,
        Reconciliation = index % 7 == 0 ? new(new('f', 32), identity, new('1', 64), "Synthetic evidence only", timestamp) : null }.Normalize();
}

sealed class FakeDriver : IResourceProcessDriver
{
    public ProcessIdentity CurrentIdentity() => new ProcessIdentity(new('a', 64), 42, "100", @"C:\fixture\owner.exe").Normalize();
    public ProcessIdentity Capture(uint pid) => throw new InvalidOperationException("The JSON fixture never captures an actual process.");
    public string State(ProcessIdentity identity) => identity == CurrentIdentity() ? "alive" : "unknown";
    public IResourceProcessHandle Open(ProcessIdentity identity) => throw new InvalidOperationException("The JSON fixture never controls processes.");
}
