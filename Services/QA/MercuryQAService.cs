using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;

namespace TeamsAudioCapture.Services.QA;

public sealed class MercuryQAService : IQAService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<QASettings> _qaSettings;
    private readonly IOptions<ApiKeysSettings> _apiKeys;

    public MercuryQAService(IHttpClientFactory httpClientFactory, IOptions<QASettings> qaSettings, IOptions<ApiKeysSettings> apiKeys)
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

        if (string.IsNullOrWhiteSpace(_apiKeys.Value.Mercury))
        {
            throw new InvalidOperationException("Mercury API key is missing in ApiKeys:Mercury.");
        }

        using var client = _httpClientFactory.CreateClient(nameof(MercuryQAService));
        client.BaseAddress = new Uri(modelSettings.BaseUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKeys.Value.Mercury);

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
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Mercury API error {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Mercury Q/A response did not contain any choices.");
        }

        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("Mercury Q/A response did not contain message content.");
        }

        var answer = contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException("Mercury Q/A response was empty.");
        }

        return answer.Trim();
    }
}
