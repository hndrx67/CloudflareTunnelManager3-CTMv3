using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CloudflareTunnelManager.Models;

namespace CloudflareTunnelManager.Services;

public sealed class CloudflareApiClient : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri("https://api.cloudflare.com/client/v4/") };
    private string _accountId = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_accountId) && _http.DefaultRequestHeaders.Authorization is not null;

    public void Configure(string accountId, string apiToken)
    {
        _accountId = accountId.Trim();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken.Trim());
    }

    public async Task<IReadOnlyList<CloudflareTunnel>> GetTunnelsAsync()
    {
        EnsureConfigured();
        using var document = await SendAsync(HttpMethod.Get, $"accounts/{_accountId}/cfd_tunnel?is_deleted=false&per_page=100");
        var tunnels = new List<CloudflareTunnel>();
        foreach (var item in document.RootElement.GetProperty("result").EnumerateArray())
        {
            var id = GetString(item, "id");
            var name = GetString(item, "name");
            var status = GetString(item, "status", "inactive");
            var source = GetString(item, "config_src");
            if (!string.IsNullOrWhiteSpace(id))
                tunnels.Add(new CloudflareTunnel { Id = id, Name = name, Status = status, ConfigSource = source });
        }
        return tunnels.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<CloudflareTunnel> CreateTunnelAsync(string name)
    {
        EnsureConfigured();
        using var document = await SendAsync(HttpMethod.Post, $"accounts/{_accountId}/cfd_tunnel",
            new { name = name.Trim(), config_src = "cloudflare" });
        var item = document.RootElement.GetProperty("result");
        return new CloudflareTunnel
        {
            Id = GetString(item, "id"),
            Name = GetString(item, "name"),
            Status = GetString(item, "status", "inactive"),
            ConfigSource = GetString(item, "config_src", "cloudflare")
        };
    }

    public async Task<IReadOnlyList<CloudflareZone>> GetZonesAsync()
    {
        EnsureConfigured();
        using var document = await SendAsync(HttpMethod.Get,
            $"zones?account.id={Uri.EscapeDataString(_accountId)}&status=active&per_page=100");
        var zones = new List<CloudflareZone>();
        foreach (var item in document.RootElement.GetProperty("result").EnumerateArray())
        {
            var id = GetString(item, "id");
            var name = GetString(item, "name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                zones.Add(new CloudflareZone { Id = id, Name = name });
        }
        return zones.OrderBy(zone => zone.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string> BindDnsAsync(string tunnelId, string hostname, IReadOnlyList<CloudflareZone> zones)
    {
        EnsureConfigured();
        hostname = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        var zone = zones
            .Where(candidate => hostname.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase) ||
                                hostname.EndsWith("." + candidate.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Name.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No matching active Cloudflare DNS zone was found for this hostname. Add Zone Read permission and ensure the domain is in this account.");

        var encodedName = Uri.EscapeDataString(hostname);
        using var existingDocument = await SendAsync(HttpMethod.Get,
            $"zones/{zone.Id}/dns_records?name={encodedName}&per_page=100");
        var records = existingDocument.RootElement.GetProperty("result");
        var target = $"{tunnelId}.cfargotunnel.com";
        var body = new { type = "CNAME", name = hostname, content = target, proxied = true, ttl = 1 };

        if (records.GetArrayLength() == 0)
        {
            await SendAsync(HttpMethod.Post, $"zones/{zone.Id}/dns_records", body);
            return $"Created proxied CNAME {hostname} → {target}";
        }

        var record = records[0];
        var recordType = GetString(record, "type");
        if (!recordType.Equals("CNAME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{hostname} already has a {recordType} record. Remove or rename that conflicting record before binding it to the tunnel.");

        var recordId = GetString(record, "id");
        await SendAsync(HttpMethod.Put, $"zones/{zone.Id}/dns_records/{recordId}", body);
        return $"Updated proxied CNAME {hostname} → {target}";
    }

    public async Task<IReadOnlyList<TunnelRoute>> GetRoutesAsync(string tunnelId)
    {
        EnsureConfigured();
        using var document = await SendAsync(HttpMethod.Get, $"accounts/{_accountId}/cfd_tunnel/{tunnelId}/configurations");
        var routes = new List<TunnelRoute>();
        var result = document.RootElement.GetProperty("result");
        if (!result.TryGetProperty("config", out var config) || !config.TryGetProperty("ingress", out var ingress)) return routes;
        foreach (var item in ingress.EnumerateArray())
        {
            var service = GetString(item, "service");
            if (service.StartsWith("http_status:", StringComparison.OrdinalIgnoreCase)) continue;
            var noTlsVerify = item.TryGetProperty("originRequest", out var origin) &&
                              origin.TryGetProperty("noTLSVerify", out var noTls) && noTls.ValueKind == JsonValueKind.True;
            routes.Add(new TunnelRoute
            {
                Hostname = GetString(item, "hostname"),
                Service = service,
                Path = GetString(item, "path"),
                NoTlsVerify = noTlsVerify
            });
        }
        return routes;
    }

    public async Task SaveRoutesAsync(string tunnelId, IEnumerable<TunnelRoute> routes)
    {
        EnsureConfigured();
        var ingress = routes.Select(route =>
        {
            var item = new Dictionary<string, object> { ["hostname"] = route.Hostname.Trim(), ["service"] = route.Service.Trim() };
            if (!string.IsNullOrWhiteSpace(route.Path)) item["path"] = route.Path.Trim();
            if (route.NoTlsVerify) item["originRequest"] = new Dictionary<string, object> { ["noTLSVerify"] = true };
            return item;
        }).ToList();
        ingress.Add(new Dictionary<string, object> { ["service"] = "http_status:404" });
        await SendAsync(HttpMethod.Put, $"accounts/{_accountId}/cfd_tunnel/{tunnelId}/configurations", new { config = new { ingress } });
    }

    public async Task<string> GetConnectorTokenAsync(string tunnelId)
    {
        EnsureConfigured();
        using var document = await SendAsync(HttpMethod.Get, $"accounts/{_accountId}/cfd_tunnel/{tunnelId}/token");
        return document.RootElement.GetProperty("result").GetString() ?? throw new InvalidOperationException("Cloudflare returned an empty connector token.");
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch { throw new InvalidOperationException($"Cloudflare returned HTTP {(int)response.StatusCode}."); }
        if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var message = "Cloudflare API request failed.";
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0 && errors[0].TryGetProperty("message", out var errorMessage))
                message = errorMessage.GetString() ?? message;
            document.Dispose();
            throw new InvalidOperationException(message);
        }
        return document;
    }

    private static string GetString(JsonElement element, string property, string fallback = "") =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new InvalidOperationException("Enter your Cloudflare Account ID and API token first.");
    }

    public void Dispose() => _http.Dispose();
}
