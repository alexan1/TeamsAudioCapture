using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TeamsAudioCapture;

public sealed class OpenAiRealtimeStreamer : ILiveAudioStreamer, IDisposable
{
    private const string RealtimeEndpoint = "wss://api.openai.com/v1/realtime";
    private const string DefaultTranscriptionModel = "gpt-4o-mini-transcribe";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly StringBuilder _inputTranscriptBuffer = new();
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _isConnected;
    private TaskCompletionSource<bool>? _setupCompletionSource;

    public OpenAiRealtimeStreamer(string apiKey, string model)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        _apiKey = apiKey;
        _model = model;
        _httpClient = new HttpClient();
    }

    /// <summary>Gets the last server error, if any.</summary>
    public string? LastServerError { get; private set; }

    /// <summary>Raised when the live provider emits a model response.</summary>
    public event Action<string>? OnResponseReceived;

    /// <summary>Raised when the provider emits an input transcription chunk.</summary>
    public event Action<string>? OnInputTranscriptReceived;

    /// <summary>Raised when a full input turn has completed.</summary>
    public event Action<string>? OnTurnComplete;

    /// <summary>Connects to the OpenAI Realtime API and initializes the session.</summary>
    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        await EstablishConnectionAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>Waits for the live session setup to complete.</summary>
    public async Task WaitForSetupCompleteAsync(CancellationToken cancellationToken)
    {
        if (_setupCompletionSource == null)
        {
            throw new InvalidOperationException("OpenAI Realtime setup state is not initialized.");
        }

        await _setupCompletionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams an audio chunk to the OpenAI Realtime API.</summary>
    public async Task StreamAudioAsync(byte[] audioData, int offset, int count, WaveFormat waveFormat)
    {
        if (!_isConnected || _webSocket == null || _cts == null)
        {
            return;
        }

        if (_setupCompletionSource != null && !_setupCompletionSource.Task.IsCompleted)
        {
            return;
        }

        var pcm16Data = ConvertToPcm16(audioData, offset, count, waveFormat);
        if (pcm16Data.Length == 0)
        {
            return;
        }

        var payload = new
        {
            type = "input_audio_buffer.append",
            audio = Convert.ToBase64String(pcm16Data)
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Streams a text answer for the specified question.</summary>
    public async Task StreamAnswerForQuestionAsync(string question, Action<string> onChunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(onChunk);

        try
        {
            var payload = new
            {
                model = "gpt-4o-mini",
                input = $"Answer this question briefly and directly:\n\nQuestion: {question}",
                stream = true
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                LastServerError = error;
                Log($"❌ OpenAI response error ({response.StatusCode}): {error}");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data: ".Length..];
                if (data == "[DONE]")
                {
                    break;
                }

                if (TryParseOutputDelta(data, out var delta))
                {
                    onChunk(delta);
                }
            }
        }
        catch (Exception ex)
        {
            LastServerError = ex.Message;
            Log($"❌ OpenAI streaming error: {ex.Message}");
        }
    }

    /// <summary>Processes an audio file and returns a full transcription.</summary>
    public Task ProcessAudioFileAsync(string filePath)
    {
        throw new NotSupportedException(Properties.Resources.OpenAiFileUploadNotSupported);
    }

    /// <summary>Disconnects from the OpenAI Realtime API.</summary>
    public async Task DisconnectAsync()
    {
        _isConnected = false;

        try
        {
            _cts?.Cancel();

            if (_receiveTask != null)
            {
                await Task.WhenAny(_receiveTask, Task.Delay(2000)).ConfigureAwait(false);
            }

            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _webSocket?.Dispose();
            _webSocket = null;
            _cts?.Dispose();
            _cts = null;
            _setupCompletionSource = null;
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _httpClient.Dispose();
    }

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _webSocket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        _setupCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var uri = new Uri($"{RealtimeEndpoint}?model={_model}");
        await _webSocket.ConnectAsync(uri, ct).ConfigureAwait(false);

        _isConnected = true;
        _receiveTask = Task.Run(() => ReceiveMessagesAsync(ct), ct);

        await SendSessionUpdateAsync(ct).ConfigureAwait(false);
    }

    private async Task SendSessionUpdateAsync(CancellationToken ct)
    {
        var payload = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text" },
                instructions = "Provide verbatim transcription of the user audio. Do not answer or summarize.",
                input_audio_transcription = new { model = DefaultTranscriptionModel },
                turn_detection = new { type = "server_vad" }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveMessagesAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];

        try
        {
            while (_webSocket != null && _webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                ms.Seek(0, SeekOrigin.Begin);
                var json = Encoding.UTF8.GetString(ms.ToArray());
                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
            Log("🛑 OpenAI receive task cancelled");
        }
        catch (Exception ex)
        {
            LastServerError = ex.Message;
            Log($"❌ OpenAI receive error: {ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
            {
                return;
            }

            switch (type)
            {
                case "session.created":
                case "session.updated":
                    _setupCompletionSource?.TrySetResult(true);
                    break;
                case "error":
                    if (root.TryGetProperty("error", out var error))
                    {
                        LastServerError = error.GetRawText();
                        Log($"❌ OpenAI error: {LastServerError}");
                        _setupCompletionSource?.TrySetException(new InvalidOperationException(LastServerError));
                    }
                    break;
                case "conversation.item.input_audio_transcription.delta":
                    HandleTranscriptionDelta(root);
                    break;
                case "conversation.item.input_audio_transcription.completed":
                    HandleTranscriptionCompleted(root);
                    break;
                case "response.text.delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        var text = delta.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            OnResponseReceived?.Invoke(text);
                        }
                    }
                    break;
            }
        }
        catch (JsonException ex)
        {
            LastServerError = ex.Message;
            Log($"⚠️ OpenAI parse error: {ex.Message}");
        }
    }

    private void HandleTranscriptionDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var delta))
        {
            return;
        }

        var text = delta.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_inputTranscriptBuffer)
        {
            _inputTranscriptBuffer.Append(text);
        }

        OnInputTranscriptReceived?.Invoke(text);
    }

    private void HandleTranscriptionCompleted(JsonElement root)
    {
        if (root.TryGetProperty("transcript", out var transcriptElement))
        {
            var transcript = transcriptElement.GetString();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                lock (_inputTranscriptBuffer)
                {
                    _inputTranscriptBuffer.Clear();
                }

                OnTurnComplete?.Invoke(transcript);
                return;
            }
        }

        string fallback;
        lock (_inputTranscriptBuffer)
        {
            fallback = _inputTranscriptBuffer.ToString().Trim();
            _inputTranscriptBuffer.Clear();
        }

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            OnTurnComplete?.Invoke(fallback);
        }
    }

    private static bool TryParseOutputDelta(string json, out string delta)
    {
        delta = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return false;
            }

            if (typeElement.GetString() != "response.output_text.delta")
            {
                return false;
            }

            if (!root.TryGetProperty("delta", out var deltaElement))
            {
                return false;
            }

            delta = deltaElement.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(delta);
        }
        catch (JsonException ex)
        {
            Log($"⚠️ OpenAI output delta parse error: {ex.Message}");
            return false;
        }
    }

    private static byte[] ConvertToPcm16(byte[] audioData, int offset, int count, WaveFormat sourceFormat)
    {
        if (sourceFormat.Encoding == WaveFormatEncoding.Pcm && sourceFormat.BitsPerSample == 16 && sourceFormat.SampleRate == 16000)
        {
            var result = new byte[count];
            Array.Copy(audioData, offset, result, 0, count);
            return result;
        }

        using var sourceStream = new RawSourceWaveStream(audioData, offset, count, sourceFormat);
        var sampleProvider = sourceStream.ToSampleProvider();

        if (sourceFormat.SampleRate != 16000)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
        }

        if (sampleProvider.WaveFormat.Channels > 1)
        {
            sampleProvider = sampleProvider.ToMono();
        }

        var pcm16Provider = new SampleToWaveProvider16(sampleProvider);

        using var outputStream = new MemoryStream();
        var buffer = new byte[16000 * 2];
        int bytesRead;

        while ((bytesRead = pcm16Provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            outputStream.Write(buffer, 0, bytesRead);
        }

        return outputStream.ToArray();
    }

    private static void Log(string message)
    {
        Console.WriteLine(message);
    }
}
