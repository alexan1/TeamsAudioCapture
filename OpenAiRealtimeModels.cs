using System;

namespace TeamsAudioCapture;

internal static class OpenAiRealtimeModels
{
    public const string DefaultModel = "gpt-realtime-mini";
    public const string DefaultLegacyReplacement = "gpt-realtime";

    public static string Normalize(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return DefaultModel;
        }

        var trimmedModel = model.Trim();

        if (trimmedModel.Equals("gpt-realtime-1.5", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultLegacyReplacement;
        }

        if (trimmedModel.StartsWith("gpt-4o-mini-realtime-preview", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultModel;
        }

        if (trimmedModel.StartsWith("gpt-4o-realtime-preview", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultLegacyReplacement;
        }

        return trimmedModel;
    }
}
