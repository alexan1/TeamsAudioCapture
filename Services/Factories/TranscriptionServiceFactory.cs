using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;
using TeamsAudioCapture.Services.Transcription;

namespace TeamsAudioCapture.Services.Factories;

public interface ITranscriptionServiceFactory
{
    ITranscriptionService Create(string selectedProvider);
}

public sealed class TranscriptionServiceFactory : ITranscriptionServiceFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<TranscriptionSettings> _settings;

    public TranscriptionServiceFactory(IServiceProvider serviceProvider, IOptions<TranscriptionSettings> settings)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
    }

    public ITranscriptionService Create(string selectedProvider)
    {
        if (string.IsNullOrWhiteSpace(selectedProvider))
        {
            selectedProvider = _settings.Value.SelectedProvider;
        }

        if (!_settings.Value.Providers.TryGetValue(selectedProvider, out var providerSettings))
        {
            throw new InvalidOperationException($"Transcription provider '{selectedProvider}' is not configured.");
        }

        return providerSettings.Provider switch
        {
            "Deepgram" => _serviceProvider.GetRequiredService<DeepgramTranscriptionService>(),
            "OpenAI" => _serviceProvider.GetRequiredService<OpenAITranscriptionService>(),
            "Gemini" => _serviceProvider.GetRequiredService<GeminiTranscriptionService>(),
            _ => throw new InvalidOperationException($"Unsupported transcription provider: {providerSettings.Provider}")
        };
    }
}
