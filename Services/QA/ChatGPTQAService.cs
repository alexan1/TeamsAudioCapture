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

        if (string.IsNullOrWhiteSpace(_apiKeys.Value.OpenAI))
        {
            throw new InvalidOperationException("OpenAI API key is missing in ApiKeys:OpenAI.");
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

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("OpenAI Q/A response did not contain any choices.");
        }

        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("OpenAI Q/A response did not contain message content.");
        }

        var answer = contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString(),
            JsonValueKind.Array => string.Concat(
                contentElement.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException("OpenAI Q/A response was empty.");
        }

        return answer.Trim();
    }
}
