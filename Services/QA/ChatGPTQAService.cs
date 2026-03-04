using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;

namespace TeamsAudioCapture.Services.QA;

public sealed class ChatGPTQAService : IQAService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<QASettings> _qaSettings;
    private readonly IOptions<ApiKeysSettings> _apiKeys;

    public ChatGPTQAService(IHttpClientFactory httpClientFactory, IOptions<QASettings> qaSettings, IOptions<ApiKeysSettings> apiKeys)
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

        using var client = _httpClientFactory.CreateClient(nameof(ChatGPTQAService));
        client.BaseAddress = new Uri(modelSettings.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKeys.Value.OpenAI);

        var payload = new
        {
            model = modelSettings.Model,
            messages = new[]
            {
                new { role = "system", content = "Answer using provided context only." },
                new { role = "user", content = $"Context: {context}\n\nQuestion: {question}" }
            }
        };

        using var response = await client.PostAsync("/v1/chat/completions", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return body;
    }
}
