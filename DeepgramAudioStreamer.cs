using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TeamsAudioCapture;

public sealed class DeepgramAudioStreamer : ILiveAudioStreamer, IDisposable
{
    private const string RealtimeEndpoint = "wss://api.deepgram.com/v1/listen";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _qnaModel;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _isConnected;
    private TaskCompletionSource<bool>? _setupCompletionSource;
    private readonly System.Text.StringBuilder _inputTranscriptBuffer = new();
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "DeepgramDebug.log");

    public string? LastServerError { get; private set; }

    public event Action<string>? OnResponseReceived;
    public event Action<string>? OnInputTranscriptReceived;
    public event Action<string>? OnTurnComplete;
    public event Action<string>? OnError;

    public DeepgramAudioStreamer(string apiKey, string model, string qnaModel)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(qnaModel);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Transcription model is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(qnaModel))
        {
            throw new ArgumentException("Q&A model is required.", nameof(qnaModel));
        }

        _apiKey = apiKey;
        _model = model;
        _qnaModel = qnaModel;
        _httpClient = new HttpClient();

        // Clear log file
        File.WriteAllText(LogFilePath, $"=== Deepgram Debug Log Started at {DateTime.Now} ===\n");
        Log($"Log file location: {LogFilePath}");
    }

    private void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        Console.WriteLine(message);
        try
        {
            File.AppendAllText(LogFilePath, logMessage);
        }
        catch { /* Ignore log errors */ }
    }

    public async Task ConnectAsync()
    {
        try
        {
            Log("🔑 Testing Deepgram API key...");
            var testUrl = "https://api.deepgram.com/v1/status";
            var request = new HttpRequestMessage(HttpMethod.Get, testUrl);
            request.Headers.Add("Authorization", $"Token {_apiKey}");

            try
            {
                var testResponse = await _httpClient.SendAsync(request);
                if (testResponse.IsSuccessStatusCode)
                {
                    Log("✅ API key is valid for Deepgram");
                }
                else
                {
                    Log($"⚠️ API key test returned status: {testResponse.StatusCode}");
                    Log($"Response: {await testResponse.Content.ReadAsStringAsync()}");
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Could not test API key: {ex.Message}");
            }

            _cts = new CancellationTokenSource();
            await EstablishConnectionAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to connect to Deepgram: {ex.Message}");
            _isConnected = false;
            throw;
        }
    }

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        _setupCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var uri = new Uri($"{RealtimeEndpoint}?model={_model}&encoding=linear16&sample_rate=16000");

        Log("🌐 Connecting to Deepgram...");
        _webSocket.Options.SetRequestHeader("Authorization", $"Token {_apiKey}");

        try
        {
            await _webSocket.ConnectAsync(uri, ct);

            _isConnected = true;
            Log("✅ Connected to Deepgram");

            _receiveTask = Task.Run(() => ReceiveMessagesAsync(ct), ct);

            // Deepgram is ready immediately
            _setupCompletionSource.TrySetResult(true);
        }
        catch (WebSocketException ex)
        {
            _isConnected = false;
            var errorMsg = $"WebSocket connection failed: {ex.Message}";
            Log($"❌ {errorMsg}");
            LastServerError = errorMsg;
            try
            {
                OnError?.Invoke(errorMsg);
            }
            catch (Exception handlerEx)
            {
                Log($"⚠️ Error handler failed: {handlerEx.Message}");
            }
            _setupCompletionSource.TrySetResult(false);
            throw;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            var errorMsg = $"Connection error: {ex.Message}";
            Log($"❌ {errorMsg}");
            LastServerError = errorMsg;
            try
            {
                OnError?.Invoke(errorMsg);
            }
            catch (Exception handlerEx)
            {
                Log($"⚠️ Error handler failed: {handlerEx.Message}");
            }
            _setupCompletionSource.TrySetResult(false);
            throw;
        }
    }

    public async Task WaitForSetupCompleteAsync(CancellationToken cancellationToken)
    {
        if (_setupCompletionSource == null)
        {
            throw new InvalidOperationException("Deepgram setup state is not initialized.");
        }

        await _setupCompletionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

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

        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(pcm16Data),
                WebSocketMessageType.Binary,
                false,
                _cts.Token
            ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"❌ Error streaming audio: {ex.Message}");
        }
    }

    public async Task StreamAnswerForQuestionAsync(string question, Action<string> onChunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(onChunk);

        try
        {
            Log($"❓ Streaming answer for: {question}");

            var payload = new
            {
                model = _qnaModel,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = $"Answer this question briefly and directly:\n\nQuestion: {question}"
                    }
                },
                stream = true
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepgram.com/v1/generate")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Token {_apiKey}");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var errorMsg = $"❌ Deepgram Q&A error ({response.StatusCode}): {error}";
                Log(errorMsg);
                LastServerError = error;
                OnError?.Invoke(errorMsg);
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null && !cancellationToken.IsCancellationRequested)
            {
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]")
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                        {
                            var text = content.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                onChunk(text);
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    Log($"⚠️ Parse error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            var errorMsg = $"❌ Deepgram streaming error: {ex.Message}";
            Log(errorMsg);
            LastServerError = ex.Message;
            try
            {
                OnError?.Invoke(errorMsg);
            }
            catch (Exception handlerEx)
            {
                Log($"⚠️ Error handler failed: {handlerEx.Message}");
            }
        }
    }

    public Task ProcessAudioFileAsync(string filePath)
    {
        throw new NotSupportedException("Deepgram file upload not yet implemented for this streamer.");
    }

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

    private async Task ReceiveMessagesAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];

        try
        {
            while (_webSocket != null && _webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                try
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                }
                catch (WebSocketException ex) when (ct.IsCancellationRequested)
                {
                    // Expected during shutdown
                    Log("🛑 Deepgram receive cancelled");
                    break;
                }
                catch (WebSocketException ex)
                {
                    // Only log and invoke error once per session, not repeatedly
                    if (_isConnected)
                    {
                        _isConnected = false;
                        LastServerError = ex.Message;
                        var errorMsg = $"❌ Deepgram WebSocket error: {ex.Message}";
                        Log(errorMsg);
                        try
                        {
                            OnError?.Invoke(errorMsg);
                        }
                        catch (Exception handlerEx)
                        {
                            Log($"⚠️ Error handler failed: {handlerEx.Message}");
                        }
                    }
                    break;
                }

                ms.Write(buffer, 0, result.Count);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("🌐 Deepgram closed connection");
                    break;
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                ms.Seek(0, SeekOrigin.Begin);
                var json = Encoding.UTF8.GetString(ms.ToArray());
                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
            Log("🛑 Deepgram receive task cancelled");
        }
        catch (Exception ex)
        {
            if (_isConnected)
            {
                _isConnected = false;
                LastServerError = ex.Message;
                var errorMsg = $"❌ Deepgram receive error: {ex.Message}";
                Log(errorMsg);
                try
                {
                    OnError?.Invoke(errorMsg);
                }
                catch (Exception handlerEx)
                {
                    Log($"⚠️ Error handler failed: {handlerEx.Message}");
                }
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();

                if (type == "Results")
                {
                    if (root.TryGetProperty("results", out var results) && results.TryGetProperty("channels", out var channels))
                    {
                        if (channels.GetArrayLength() > 0)
                        {
                            var channel = channels[0];
                            if (channel.TryGetProperty("alternatives", out var alternatives) && alternatives.GetArrayLength() > 0)
                            {
                                var alternative = alternatives[0];
                                if (alternative.TryGetProperty("transcript", out var transcript))
                                {
                                    var text = transcript.GetString();
                                    if (!string.IsNullOrWhiteSpace(text))
                                    {
                                        _inputTranscriptBuffer.Append(text);
                                        OnInputTranscriptReceived?.Invoke(text);
                                    }
                                }
                            }
                        }
                    }

                    if (root.TryGetProperty("is_final", out var isFinal) && isFinal.GetBoolean())
                    {
                        var finalTranscript = _inputTranscriptBuffer.ToString().Trim();
                        _inputTranscriptBuffer.Clear();

                        if (!string.IsNullOrWhiteSpace(finalTranscript))
                        {
                            OnTurnComplete?.Invoke(finalTranscript);
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            Log($"⚠️ Deepgram parse error: {ex.Message}");
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
}
