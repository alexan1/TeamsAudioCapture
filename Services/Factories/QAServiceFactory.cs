using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamsAudioCapture.Configuration;
using TeamsAudioCapture.Services.QA;

namespace TeamsAudioCapture.Services.Factories;

public interface IQAServiceFactory
{
    IQAService Create(string selectedModel);
}

public sealed class QAServiceFactory : IQAServiceFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<QASettings> _settings;

    public QAServiceFactory(IServiceProvider serviceProvider, IOptions<QASettings> settings)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
    }

    public IQAService Create(string selectedModel)
    {
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            selectedModel = _settings.Value.SelectedModel;
        }

        if (!_settings.Value.Models.TryGetValue(selectedModel, out var modelSettings))
        {
            throw new InvalidOperationException($"QA model '{selectedModel}' is not configured.");
        }

        return modelSettings.Provider switch
        {
            "Claude" => _serviceProvider.GetRequiredService<ClaudeQAService>(),
            "ChatGPT" => _serviceProvider.GetRequiredService<ChatGPTQAService>(),
            "Mercury" => _serviceProvider.GetRequiredService<MercuryQAService>(),
            _ => throw new InvalidOperationException($"Unsupported QA provider: {modelSettings.Provider}")
        };
    }
}
