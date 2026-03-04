using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;

namespace TeamsAudioCapture.Services.QA;

public sealed class ClaudeQAService : IQAService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<QASettings> _qaSettings;
    private readonly IOptions<ApiKeysSettings> _apiKeys;

    public ClaudeQAService(IHttpClientFactory httpClientFactory, IOptions<QASettings> qaSettings, IOptions<ApiKeysSettings> apiKeys)
    {
        _httpClientFactory = httpClientFactory;
        _qaSettings = qaSettings;
        _apiKeys = apiKeys;
    }

    public async Task<string> AskAsync(string question, string context, CancellationToken cancellationToken = default)
    {
        var selected = _qaSettings.Value.SelectedModel;
        if (!_qaSettings.Value.Models.TryGetValue(selected, out var modelSettings))
        {
            throw new InvalidOperationException($"QA model '{selected}' is not configured.");
        }

        using var client = _httpClientFactory.CreateClient(nameof(ClaudeQAService));
        client.BaseAddress = new Uri(modelSettings.BaseUrl);
        client.DefaultRequestHeaders.Add("x-api-key", _apiKeys.Value.Claude);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var payload = new
        {
            model = modelSettings.Model,
            max_tokens = 512,
            messages = new[]
            {
                new { role = "user", content = $"Context: {context}\n\nQuestion: {question}" }
            }
        };

        using var response = await client.PostAsync("/v1/messages", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return body;
    }
}
