using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamsAudioCapture.Configuration;
using TeamsAudioCapture.Services.Factories;
using TeamsAudioCapture.Services.QA;
using TeamsAudioCapture.Services.Transcription;

namespace TeamsAudioCapture;

public static class Program
{
	public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<TranscriptionSettings>(configuration.GetSection("Transcription"));
		services.Configure<QASettings>(configuration.GetSection("QA"));
		services.Configure<ApiKeysSettings>(configuration.GetSection("ApiKeys"));

		services.AddHttpClient();

		services.AddTransient<DeepgramTranscriptionService>();
		services.AddTransient<OpenAITranscriptionService>();
		services.AddTransient<GeminiTranscriptionService>();

		services.AddTransient<ClaudeQAService>();
		services.AddTransient<ChatGPTQAService>();
		services.AddTransient<MercuryQAService>();

		services.AddSingleton<ITranscriptionServiceFactory, TranscriptionServiceFactory>();
		services.AddSingleton<IQAServiceFactory, QAServiceFactory>();

		services.AddSingleton<MainWindow>();
	}
}
