namespace TeamsAudioCapture.Services.Transcription;

public interface ITranscriptionService
{
    event Action<string>? OnTranscriptReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
