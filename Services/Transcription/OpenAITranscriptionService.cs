using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;

namespace TeamsAudioCapture.Services.Transcription;

public sealed class OpenAITranscriptionService : ITranscriptionService
{
    private readonly IOptions<TranscriptionSettings> _transcriptionSettings;
    private readonly IOptions<ApiKeysSettings> _apiKeys;
    private bool _started;

    public OpenAITranscriptionService(IOptions<TranscriptionSettings> transcriptionSettings, IOptions<ApiKeysSettings> apiKeys)
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

        if (string.IsNullOrWhiteSpace(_apiKeys.Value.OpenAI))
        {
            throw new InvalidOperationException("OpenAI API key is missing in ApiKeys:OpenAI.");
        }

        _started = true;
        OnTranscriptReceived?.Invoke("OpenAI realtime transcription started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        return Task.CompletedTask;
    }
}
