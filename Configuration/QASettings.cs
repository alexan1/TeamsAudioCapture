namespace TeamsAudioCapture.Configuration;

public sealed class QASettings
{
    public string SelectedModel { get; set; } = string.Empty;
    public Dictionary<string, QAModelSettings> Models { get; set; } = new(StringComparer.Ordinal);
}

public sealed class QAModelSettings
{
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
}
