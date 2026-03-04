namespace TeamsAudioCapture.Configuration;

public sealed class TranscriptionSettings
{
    public string SelectedProvider { get; set; } = string.Empty;
    public Dictionary<string, TranscriptionProviderSettings> Providers { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TranscriptionProviderSettings
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string WebSocketUrl { get; set; } = string.Empty;
}
