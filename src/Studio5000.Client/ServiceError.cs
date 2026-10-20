using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Studio5000.Core;

namespace Studio5000.Client;

public sealed record ServiceError(string Code, string Message, int HttpStatus, string[] WrapperCodes)
{
    // Only the shared service's bounded code/message envelope is exposed to MCP.
    // Unknown HTTP bodies and transport exceptions retain generic host diagnostics.
    public static ServiceError Parse(HttpStatusCode status, string body)
    {
        var wrappers = new List<string>();
        bool Envelope(string json, out string code, out string message)
        {
            code = message = ""; if (json.Length > 16384) return false;
            try
            {
                using var parsed = JsonDocument.Parse(json, new() { MaxDepth = 8 }); var root = parsed.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var c) || c.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.String) return false;
                code = c.GetString()!; message = m.GetString()!;
                return Regex.IsMatch(code, "^[a-z][a-z0-9_]{0,79}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) && message.Length is > 0 and <= 8192;
            }
            catch (JsonException) { return false; }
        }
        if (!Envelope(body, out var code, out var message))
            return new("service_http_error", "Service request failed (HTTP " + (int)status + "); inspect the local service logs for details.", (int)status, []);
        for (var i = 0; i < 4 && code == "extension_operation_failed"; i++)
        {
            if (!Envelope(message, out var innerCode, out var innerMessage)) break;
            wrappers.Add(code); code = innerCode; message = innerMessage;
        }
        return new(code, message, (int)status, wrappers.ToArray());
    }
    public string ToJson() => JsonSerializer.Serialize(this, ProjectService.Json);
}

// Keep the envelope in Message too: extension workers transport exception text,
// while GUI/API clients can use the typed Error property.
public sealed class ServiceErrorException(ServiceError error) : HttpRequestException(error.ToJson(), null, (HttpStatusCode)error.HttpStatus)
{
    public ServiceError Error { get; } = error;
}
