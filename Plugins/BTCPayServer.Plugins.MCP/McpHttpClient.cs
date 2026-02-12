using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

namespace BTCPayServer.Plugins.MCP;

public class McpHttpClient
{
    private readonly HttpClient _httpClient;

    public McpHttpClient(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HttpContext available");

        var request = httpContext.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        _httpClient = httpClientFactory.CreateClient("McpGreenfield");
        _httpClient.BaseAddress = new Uri(baseUrl);

        var authHeader = request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
        }
    }

    public async Task<string> GetAsync(string path)
    {
        var response = await _httpClient.GetAsync(path);
        return await HandleResponse(response);
    }

    public async Task<string> PostAsync(string path, object? body = null)
    {
        var content = Serialize(body);
        var response = await _httpClient.PostAsync(path, content);
        return await HandleResponse(response);
    }

    public async Task<string> PutAsync(string path, object? body = null)
    {
        var content = Serialize(body);
        var response = await _httpClient.PutAsync(path, content);
        return await HandleResponse(response);
    }

    public async Task<string> DeleteAsync(string path)
    {
        var response = await _httpClient.DeleteAsync(path);
        return await HandleResponse(response);
    }

    public async Task<string> PatchAsync(string path, object? body = null)
    {
        var content = Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = content };
        var response = await _httpClient.SendAsync(request);
        return await HandleResponse(response);
    }

    private static StringContent? Serialize(object? body)
    {
        if (body is null) return null;
        return new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
    }

    private static async Task<string> HandleResponse(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return string.IsNullOrEmpty(body) ? "{\"success\": true}" : body;

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Authentication failed. Check your API key.",
            HttpStatusCode.Forbidden => $"Permission denied. {ExtractError(body)}",
            HttpStatusCode.NotFound => $"Not found. {ExtractError(body)}",
            HttpStatusCode.UnprocessableEntity => $"Validation error: {ExtractError(body)}",
            _ => $"API error ({(int)response.StatusCode}): {ExtractError(body)}"
        };

        throw new McpException(message);
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrEmpty(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString() ?? "";
            return body;
        }
        catch
        {
            return body;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
