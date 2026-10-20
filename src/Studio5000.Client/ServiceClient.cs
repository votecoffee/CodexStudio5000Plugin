using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Studio5000.Core;

namespace Studio5000.Client;

public sealed class ServiceClient : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<ServiceErrorException, Exception>? errorTranslator;
    public ServiceClient(string root, Func<ServiceErrorException, Exception>? errorTranslator = null)
    {
        this.errorTranslator = errorTranslator;
        var file = Path.Combine(root, ".local", "connection.json");
        if (!File.Exists(file)) throw new InvalidOperationException("Start scripts/Start-Service.ps1 first.");
        using var connection = JsonDocument.Parse(File.ReadAllText(file));
        var url = new Uri(connection.RootElement.GetProperty("url").GetString()!);
        if (url.Scheme != "http" || !System.Net.IPAddress.TryParse(url.Host, out var ip) || !System.Net.IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("Connection must use a loopback IP address.");
        http = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(240) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            File.ReadAllText(connection.RootElement.GetProperty("tokenFile").GetString()!).Trim());
    }
    public static string FindRoot() => Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(path, ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ProjectService.Json, ct))!;
    }
    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(path, body, ProjectService.Json, ct);
        await Check(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ProjectService.Json, ct))!;
    }
    public async Task<byte[]> DownloadAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync("/api/artifacts/" + id, ct);
        await Check(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
    private async Task Check(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = new ServiceErrorException(ServiceError.Parse(response.StatusCode, await response.Content.ReadAsStringAsync(ct)));
            throw errorTranslator?.Invoke(error) ?? error;
        }
    }
    public void Dispose() => http.Dispose();
}
