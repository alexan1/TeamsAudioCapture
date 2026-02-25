using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace TeamsAudioCapture;

public interface ILiveAudioStreamer
{
    /// <summary>Gets the last server error, if any.</summary>
    string? LastServerError { get; }

    /// <summary>Raised when the live provider emits a model response.</summary>
    event Action<string>? OnResponseReceived;

    /// <summary>Raised when the provider emits an input transcription chunk.</summary>
    event Action<string>? OnInputTranscriptReceived;

    /// <summary>Raised when a full input turn has completed.</summary>
    event Action<string>? OnTurnComplete;

    /// <summary>Connects to the live API and initializes the session.</summary>
    Task ConnectAsync();

    /// <summary>Waits for the live session setup to complete.</summary>
    Task WaitForSetupCompleteAsync(CancellationToken cancellationToken);

    /// <summary>Streams an audio chunk to the live provider.</summary>
    Task StreamAudioAsync(byte[] audioData, int offset, int count, WaveFormat waveFormat);

    /// <summary>Streams a text answer for the specified question.</summary>
    Task StreamAnswerForQuestionAsync(string question, Action<string> onChunk, CancellationToken cancellationToken = default);

    /// <summary>Processes an audio file and returns a full transcription.</summary>
    Task ProcessAudioFileAsync(string filePath);

    /// <summary>Disconnects from the live provider.</summary>
    Task DisconnectAsync();
}
