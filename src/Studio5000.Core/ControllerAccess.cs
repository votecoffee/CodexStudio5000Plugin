namespace Studio5000.Core;

public static class ControllerAccess
{
    // An explicit path is a target selection, never a discovery/network probe.
    public static ControllerTarget? Resolve(ServiceOptions options, string? id)
    {
        if (id is null) return null;
        var configured = options.ControllerTargets.SingleOrDefault(t => t.Id == id);
        if (configured is not null) return configured;
        if (options.AllowAnyControllerPath && id.StartsWith("path:", StringComparison.Ordinal))
        {
            var path = id[5..];
            if (path.Length is < 1 or > 2048 || path.Trim() != path || path.Any(char.IsControl))
                throw new OperationException("sdk_target_invalid", "Supply path: followed by an explicit, nonempty communications path without control characters.");
            return new(id, path);
        }
        throw new OperationException("sdk_target_unknown", "Select a configured controller target, or enable explicit path: targets in Settings.");
    }
}
