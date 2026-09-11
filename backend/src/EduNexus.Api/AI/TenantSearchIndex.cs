using System.Text;
using System.Text.Json;

namespace EduNexus.Api.AI;

public sealed class AiOptions
{
    public const string Section = "AI";
    /// <summary>Echo (extractive, no LLM) or OpenAI (OpenAI-compatible chat completions).</summary>
    public string Provider { get; set; } = "Echo";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string OpenSearchUrl { get; set; } = "http://127.0.0.1:9201";
}

/// <summary>Tenant-scoped retrieval index on OpenSearch (AI-06). One index per tenant.</summary>
public sealed class TenantSearchIndex(HttpClient http, AiOptions options)
{
    public static string IndexFor(Guid tenantId) => $"edunexus-{tenantId:N}";

    public async Task EnsureIndexAsync(Guid tenantId, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            settings = new { number_of_shards = 1, analysis = new { analyzer = new { content_analyzer = new { type = "custom", tokenizer = "standard", filter = new[] { "lowercase", "arabic_normalization" } } } } },
            mappings = new { properties = new
            {
                kind = new { type = "keyword" },
                entityId = new { type = "keyword" },
                title = new { type = "text", analyzer = "content_analyzer" },
                text = new { type = "text", analyzer = "content_analyzer" },
            } },
        });
        using var res = await http.PutAsync($"{options.OpenSearchUrl}/{IndexFor(tenantId)}",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task IndexAsync(Guid tenantId, string kind, Guid entityId, string title, string text, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { kind, entityId, title, text });
        using var res = await http.PostAsync($"{options.OpenSearchUrl}/{IndexFor(tenantId)}/_doc?refresh=true",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<List<(string Kind, string Title, string Text)>> SearchAsync(Guid tenantId, string q, int size = 5, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            size,
            query = new { multi_match = new { query = q, fields = new[] { "title^2", "text" } } },
        });
        using var res = await http.PostAsync($"{options.OpenSearchUrl}/{IndexFor(tenantId)}/_search",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!res.IsSuccessStatusCode) return [];
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
            .Select(h => h.GetProperty("_source"))
            .Select(s => (s.GetProperty("kind").GetString() ?? "?", s.GetProperty("title").GetString() ?? "", s.GetProperty("text").GetString() ?? ""))
            .ToList();
    }
}
