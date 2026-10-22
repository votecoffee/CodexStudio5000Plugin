using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Studio5000.Core.Resources;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProcessIdentity(
    [property: JsonPropertyName("host_id")] string HostId,
    [property: JsonPropertyName("pid")] uint Pid,
    [property: JsonPropertyName("created")] string Created,
    [property: JsonPropertyName("executable")] string Executable)
{
    public ProcessIdentity Normalize()
    {
        ResourceRules.Token(HostId, 64); ResourceRules.Text(Executable, 1, 32768);
        if (Pid == 0 || !Regex.IsMatch(Created ?? "", "^[0-9]{1,24}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid process creation identity.");
        if (!Path.IsPathFullyQualified(Executable) || Executable.Contains('\0')) throw new ArgumentException("An absolute process image is required.");
        return this with { Created = BigInteger.Parse(Created!, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), Executable = Path.GetFullPath(Executable).ToLowerInvariant() };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CleanupReceipt(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("expected_revision")] string ExpectedRevision,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("message")] string Message)
{
    public void Validate()
    {
        ResourceRules.Token(RequestId, 32); ResourceRules.Token(ExpectedRevision, 64);
        ResourceRules.Choice(Outcome, "dispatching", "terminated", "already_exited", "uncertain"); ResourceRules.Text(Message, 0, 512);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceReconciliation(
    [property: JsonPropertyName("actor_owner_id")] string ActorOwnerId,
    [property: JsonPropertyName("actor")] ProcessIdentity Actor,
    [property: JsonPropertyName("previous_revision")] string PreviousRevision,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("recorded_at"), JsonRequired] double RecordedAt)
{
    public ResourceReconciliation Normalize()
    {
        ResourceRules.Token(ActorOwnerId, 32); ResourceRules.Token(PreviousRevision, 64);
        ResourceRules.Text(Evidence, 1, 1024); ResourceRules.Finite(RecordedAt);
        return this with { Actor = (Actor ?? throw new ArgumentException("Missing recovery actor.")).Normalize() };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceRecord
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("resource_id")] public string ResourceId { get; init; } = "";
    [JsonPropertyName("app_id")] public string AppId { get; init; } = "";
    [JsonPropertyName("owner_id")] public string OwnerId { get; init; } = "";
    [JsonPropertyName("owner")] public ProcessIdentity Owner { get; init; } = null!;
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "process";
    [JsonPropertyName("ownership")] public string Ownership { get; init; } = "private";
    [JsonPropertyName("cleanup_mode")] public string CleanupMode { get; init; } = "report";
    [JsonPropertyName("state")] public string State { get; init; } = "launching";
    [JsonPropertyName("process")] public ProcessIdentity? Process { get; init; }
    [JsonPropertyName("members")] public ProcessIdentity[] Members { get; init; } = [];
    [JsonPropertyName("containment")] public string Containment { get; init; } = "app_owned";
    [JsonPropertyName("recovery_barrier")] public string? RecoveryBarrier { get; init; }
    [JsonPropertyName("receipt")] public CleanupReceipt? Receipt { get; init; }
    [JsonPropertyName("reconciliation")] public ResourceReconciliation? Reconciliation { get; init; }
    [JsonPropertyName("barrier_resolution")] public ResourceReconciliation? BarrierResolution { get; init; }
    [JsonPropertyName("created_at")] public double CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public double UpdatedAt { get; init; }
    [JsonIgnore] public string Revision => ResourceJson.Digest(this);
    [JsonIgnore] public bool Terminal => State is "released" or "terminated" or "failed";

    public ResourceRecord Normalize()
    {
        if (SchemaVersion != 1) throw new ArgumentException("Resource schema_version must be integer 1.");
        ResourceRules.Token(ResourceId, 32); ResourceRules.Token(OwnerId, 32); ResourceRules.Name(AppId); ResourceRules.Name(Kind);
        ResourceRules.Text(Label, 1, 256); ResourceRules.Finite(CreatedAt); ResourceRules.Finite(UpdatedAt);
        ResourceRules.Choice(Ownership, "private", "attached", "shared"); ResourceRules.Choice(CleanupMode, "terminate", "report");
        ResourceRules.Choice(State, "launching", "active", "released", "terminated", "failed", "uncertain");
        ResourceRules.Choice(Containment, "app_owned", "windows_job");
        if (Members is null || Members.Length > 256) throw new ArgumentException("Resource membership exceeds its budget.");
        if (RecoveryBarrier is not null) ResourceRules.Text(RecoveryBarrier, 0, 512);
        Receipt?.Validate();
        var owner = (Owner ?? throw new ArgumentException("Missing resource owner.")).Normalize();
        var process = Process?.Normalize(); var members = Members.Select(m => m.Normalize()).ToArray();
        if (Ownership != "private" && CleanupMode != "report") throw new ArgumentException("Attached/shared resources are report-only.");
        if (process == owner) throw new ArgumentException("A resource cannot be its own owner process.");
        foreach (var identity in process is null ? members : members.Append(process))
        {
            if (identity.HostId != owner.HostId) throw new ArgumentException("Resource and owner must belong to the same host.");
            if (Ownership == "private" && BigInteger.Parse(identity.Created, CultureInfo.InvariantCulture) < BigInteger.Parse(owner.Created, CultureInfo.InvariantCulture))
                throw new ArgumentException("A private process cannot predate its owning supervisor.");
        }
        return this with { Owner = owner, Process = process, Members = members, Reconciliation = Reconciliation?.Normalize(), BarrierResolution = BarrierResolution?.Normalize() };
    }
}

internal static class ResourceRules
{
    internal static void Token(string? value, int length)
    { if (value is null || value.Length != length || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Invalid resource token."); }
    internal static void Name(string? value)
    { if (value is null || !Regex.IsMatch(value, "^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Invalid resource name."); }
    internal static void Choice(string? value, params string[] choices)
    { if (!choices.Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Invalid resource policy/state."); }
    internal static void Finite(double value)
    { if (!double.IsFinite(value)) throw new ArgumentException("Resource timestamps must be finite."); }
    internal static void Text(string? value, int minimum, int maximum)
    {
        if (value is null) throw new ArgumentException("Missing resource text.");
        // Python bounds Unicode code points, not UTF-16 code units. Refuse invalid
        // surrogate sequences instead of silently changing a retained identity.
        try { _ = new System.Text.UnicodeEncoding(false, false, true).GetByteCount(value); }
        catch (System.Text.EncoderFallbackException ex) { throw new ArgumentException("Invalid resource Unicode.", ex); }
        var count = 0; foreach (var _ in value.EnumerateRunes()) count++;
        if (count < minimum || count > maximum) throw new ArgumentException("Resource text exceeds its bounds.");
    }
}
