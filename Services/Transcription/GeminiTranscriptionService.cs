using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;

namespace TeamsAudioCapture.Services.Transcription;

public sealed class GeminiTranscriptionService : ITranscriptionService
{
    private readonly IOptions<TranscriptionSettings> _transcriptionSettings;
    private readonly IOptions<ApiKeysSettings> _apiKeys;
    private bool _started;

    public GeminiTranscriptionService(IOptions<TranscriptionSettings> transcriptionSettings, IOptions<ApiKeysSettings> apiKeys)
    {
        _transcriptionSettings = transcriptionSettings;
        _apiKeys = apiKeys;
    }

    public event Action<string>? OnTranscriptReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_apiKeys.Value.Gemini))
        {
            throw new InvalidOperationException("Gemini API key is missing in ApiKeys:Gemini.");
        }

        _started = true;
        OnTranscriptReceived?.Invoke("Gemini live transcription started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        return Task.CompletedTask;
    }
}
