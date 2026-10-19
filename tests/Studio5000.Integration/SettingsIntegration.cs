using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Core;

internal static class SettingsIntegration
{
    public static async Task<List<string>> RunAsync(ServiceClient client)
    {
        var checks = new List<string>();
        void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); checks.Add(text); Console.WriteLine("PASS: " + text); }
        var original = await client.GetAsync<ServiceSettingsSnapshot>("/api/settings");
        Check(!original.RestartRequired, "Settings distinguish saved and running configuration without a false restart requirement: " + string.Join(", ", original.ChangedSettings));
        var broad = await client.PostAsync<JsonElement>("/api/settings/broad-local-preview", original.Saved);
        Check((await client.GetAsync<ServiceSettingsSnapshot>("/api/settings")).Revision == original.Revision, "Broad local profile preview does not save or change access");
        try
        {
            var saved = await client.PostAsync<ServiceSettingsSaveResult>("/api/settings", new ServiceSettingsSaveRequest(original.Revision, broad));
            Check(saved.Settings.RestartRequired && saved.BackupPath is not null &&
                !saved.Settings.Effective.GetProperty("Studio5000").GetProperty("EnableControllerOperations").GetBoolean(), "Settings save retains a backup and stages broad access without enabling the running service");
            var caps = await client.GetAsync<JsonElement>("/api/sdk/capabilities");
            Check(!caps.GetProperty("controllerOperationsEnabled").GetBoolean() && !caps.GetProperty("allowAnyControllerPath").GetBoolean(), "Running SDK policy remains unchanged after a settings save");
            var requests = new[] { 181, 182 }.Select(async seconds =>
            {
                var proposed = JsonNode.Parse(broad.GetRawText())!; proposed["Studio5000"]!["NativeTimeoutSeconds"] = seconds;
                try { await client.PostAsync<ServiceSettingsSaveResult>("/api/settings", new ServiceSettingsSaveRequest(saved.Settings.Revision, JsonSerializer.SerializeToElement(proposed))); return "saved"; }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict) { return "conflict"; }
            });
            var results = await Task.WhenAll(requests);
            Check(results.Count(r => r == "saved") == 1 && results.Count(r => r == "conflict") == 1, "Concurrent settings saves with one revision produce one winner and one explicit conflict");
            var current = await client.GetAsync<ServiceSettingsSnapshot>("/api/settings"); var invalid = JsonNode.Parse(current.Saved.GetRawText())!; invalid["ServiceUrl"] = "http://192.0.2.1:5487";
            var rejected = false;
            try { await client.PostAsync<ServiceSettingsSaveResult>("/api/settings", new ServiceSettingsSaveRequest(current.Revision, JsonSerializer.SerializeToElement(invalid))); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest) { rejected = true; }
            Check(rejected && (await client.GetAsync<ServiceSettingsSnapshot>("/api/settings")).Revision == current.Revision, "Even broad access cannot save a non-loopback service URL");
        }
        finally
        {
            var current = await client.GetAsync<ServiceSettingsSnapshot>("/api/settings");
            await client.PostAsync<ServiceSettingsSaveResult>("/api/settings", new ServiceSettingsSaveRequest(current.Revision, original.Saved));
        }
        Check(!(await client.GetAsync<ServiceSettingsSnapshot>("/api/settings")).RestartRequired, "Restoring the original settings clears staged changes without restarting or connecting");
        return checks;
    }
}
