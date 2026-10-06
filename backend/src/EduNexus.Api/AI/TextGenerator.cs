using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EduNexus.Api.AI;

/// <summary>
/// Optional generative step for AI assistants (docs/09-ai). <c>Echo</c> never calls a model: callers use their
/// grounded extractive draft. <c>OpenAI</c> calls an OpenAI-compatible <c>/chat/completions</c> endpoint
/// configured by AI:BaseUrl/ApiKey/Model; any failure falls back to the extractive draft.
/// </summary>
public sealed class TextGenerator(HttpClient http, AiOptions options, ILogger<TextGenerator> log)
{
    public const string ExtractiveModel = "edunexus-extractive-v1";

    public bool IsGenerative =>
        options.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(options.BaseUrl) && !string.IsNullOrWhiteSpace(options.Model);

    /// <summary>Returns (text, model). Falls back to <paramref name="extractiveDraft"/> when no model is configured or the call fails.</summary>
    public async Task<(string Text, string Model)> GenerateAsync(string instruction, string groundedContext, string extractiveDraft, CancellationToken ct)
    {
        if (!IsGenerative) return (extractiveDraft, ExtractiveModel);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl.TrimEnd('/') + "/chat/completions");
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            var body = new
            {
                model = options.Model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content =
                        "You assist a university governance secretariat. Use ONLY the facts in the provided record. " +
                        "Do not invent names, numbers, decisions or dates. If something is not in the record, say it is not recorded. " +
                        "Your output is a draft for human review." },
                    new { role = "user", content = $"{instruction}\n\n--- RECORD ---\n{groundedContext}" },
                },
            };
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var res = await http.SendAsync(req, cts.Token);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(text) ? (extractiveDraft, ExtractiveModel) : (text.Trim(), options.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "AI provider call failed; using the extractive draft");
            return (extractiveDraft, ExtractiveModel);
        }
    }
}
