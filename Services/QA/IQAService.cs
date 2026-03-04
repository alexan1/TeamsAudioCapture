namespace TeamsAudioCapture.Services.QA;

public interface IQAService
{
    Task<string> AskAsync(string question, string context, CancellationToken cancellationToken = default);
}
