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
using TeamsAudioCapture;

public class GeminiAudioStreamer : IDisposable, ILiveAudioStreamer
{
    private const string DefaultLiveModel = "models/gemini-2.5-flash-native-audio-preview-12-2025";
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isConnected;
    private Task? _receiveTask;
    private WaveFormat? _currentWaveFormat;
    private TaskCompletionSource<bool>? _setupCompletionSource;
    private DateTime _lastTranscriptionTime;
    private bool _liveConnectFailed;
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "GeminiDebug.log");

    public string? LastServerError { get; private set; }

    public event Action<string>? OnResponseReceived;
    public event Action<string>? OnInputTranscriptReceived;
    public event Action<string>? OnTurnComplete;

    private readonly System.Text.StringBuilder _inputTranscriptBuffer = new();

    public GeminiAudioStreamer(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _apiKey = apiKey;
        _httpClient = new HttpClient();

        // Clear log file
        File.WriteAllText(LogFilePath, $"=== Gemini Debug Log Started at {DateTime.Now} ===\n");
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
            // First, test if API key works with regular API
            Log("🔑 Testing API key with regular Gemini API first...");
            var testUrl = $"https://generativelanguage.googleapis.com/v1/models?key={_apiKey}";
            var testResponse = await _httpClient.GetAsync(testUrl);
            if (testResponse.IsSuccessStatusCode)
            {
                Log("✅ API key is valid for regular Gemini API");
            }
            else
            {
                var error = await testResponse.Content.ReadAsStringAsync();
                Log($"❌ API key test failed: {testResponse.StatusCode}");
                Log($"   Error: {error}");
            }

            _cts = new CancellationTokenSource();
            await EstablishConnectionAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Log($"❌ Failed to connect to Gemini: {ex.Message}");
            _isConnected = false;
            throw;
        }
    }

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        _setupCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var uri = new Uri($"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent?key={_apiKey}");

        Log("🌐 Connecting to Gemini Live API...");
        await _webSocket.ConnectAsync(uri, ct);

        _isConnected = true;
        Log("✅ Connected to Gemini Live API");

        _receiveTask = Task.Run(() => ReceiveMessagesAsync(ct), ct);

        await SendSetupMessageAsync(ct);
        Log("⏳ Setup sent; waiting for live responses...");
    }

    private async Task ReconnectAsync()
    {
        if (_cts == null || _cts.IsCancellationRequested)
            return;

        var delay = TimeSpan.FromSeconds(2);
        const int maxRetries = 5;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (_cts.IsCancellationRequested)
                return;

            try
            {
                Log($"🔄 Reconnect attempt {attempt}/{maxRetries}...");

                lock (_inputTranscriptBuffer)
                    _inputTranscriptBuffer.Clear();

                await EstablishConnectionAsync(_cts.Token);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
                await _setupCompletionSource!.Task.WaitAsync(linked.Token);

                Log("✅ Reconnected successfully");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !_cts.IsCancellationRequested)
            {
                _isConnected = false;
                Log($"⚠️ Reconnect attempt {attempt} failed: {ex.Message}. Retrying in {delay.TotalSeconds}s...");
                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
            catch (Exception ex)
            {
                _isConnected = false;
                Log($"❌ All reconnect attempts failed: {ex.Message}");
                return;
            }
        }
    }

    public async Task StreamAudioAsync(byte[] audioData, int offset, int count, WaveFormat waveFormat)
    {
        if (!_isConnected || _webSocket == null || _cts == null)
        {
            return;
        }

        // Skip sending if setup hasn't completed yet (non-blocking check)
        if (_setupCompletionSource != null && !_setupCompletionSource.Task.IsCompleted)
        {
            return;
        }

        try
        {
            // Store wave format for first call
            if (_currentWaveFormat == null)
            {
                _currentWaveFormat = waveFormat;
            }

            // Convert audio to PCM16 if needed and send immediately
            var pcm16Data = ConvertToPCM16(audioData, offset, count, waveFormat);
            var base64Audio = Convert.ToBase64String(pcm16Data);

            var message = new
            {
                realtimeInput = new
                {
                    mediaChunks = new[]
                    {
                        new
                        {
                            mimeType = "audio/pcm;rate=16000",
                            data = base64Audio
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(message);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                _cts.Token
            );
        }
        catch (Exception ex)
        {
            Log($"❌ Error streaming audio: {ex.Message}");
        }
    }

    public async Task WaitForSetupCompleteAsync(CancellationToken cancellationToken)
    {
        if (_setupCompletionSource == null)
        {
            throw new InvalidOperationException("Live API setup state is not initialized.");
        }

        if (_setupCompletionSource.Task.IsCompleted)
        {
            await _setupCompletionSource.Task;
            return;
        }

        Log("⏳ Waiting for setup completion...");
        try
        {
            await _setupCompletionSource.Task.WaitAsync(cancellationToken);
            Log("✅ Setup wait completed successfully");
        }
        catch (OperationCanceledException)
        {
            Log("❌ Setup wait was canceled or timed out");
            throw;
        }
        catch (Exception ex)
        {
            Log($"❌ Setup wait failed: {ex.Message}");
            throw;
        }
    }

    private async Task SendSetupMessageAsync(CancellationToken ct)
    {
        try
        {
            var setup = new
            {
                setup = new
                {
                    model = DefaultLiveModel,
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" }
                    },
                    inputAudioTranscription = new { },
                    systemInstruction = new
                    {
                        parts = new[]
                        {
                            new { text = "Listen to the user and do not speak" }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(setup);
            var bytes = Encoding.UTF8.GetBytes(json);

            Log($"📤 Sending setup message: {json}");

            await _webSocket!.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                ct
            );

            Log("✅ Setup message sent successfully");
        }
        catch (Exception ex)
        {
            Log($"❌ Error sending setup: {ex.Message}");
            _setupCompletionSource?.TrySetException(ex);
            throw;
        }
    }

    private byte[] ConvertToPCM16(byte[] audioData, int offset, int count, WaveFormat sourceFormat)
    {
        // If already PCM16 at correct sample rate, return as-is
        if (sourceFormat.Encoding == WaveFormatEncoding.Pcm && 
            sourceFormat.BitsPerSample == 16 && 
            sourceFormat.SampleRate == 16000)
        {
            var result = new byte[count];
            Array.Copy(audioData, offset, result, 0, count);
            return result;
        }

        try
        {
            // Convert to PCM16 16kHz mono
            using var sourceStream = new RawSourceWaveStream(audioData, offset, count, sourceFormat);
            var sampleProvider = sourceStream.ToSampleProvider();

            // Resample to 16kHz if needed
            if (sourceFormat.SampleRate != 16000)
            {
                sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 16000);
            }

            // Convert to mono if stereo
            if (sampleProvider.WaveFormat.Channels > 1)
            {
                sampleProvider = sampleProvider.ToMono();
            }

            // Convert to 16-bit PCM
            var pcm16Provider = new SampleToWaveProvider16(sampleProvider);

            using var outputStream = new MemoryStream();
            var buffer = new byte[16000 * 2]; // 1 second buffer
            int bytesRead;

            while ((bytesRead = pcm16Provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                outputStream.Write(buffer, 0, bytesRead);
            }

            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            Log($"⚠️ Error converting audio format: {ex.Message}");
            // Return original data as fallback
            var fallback = new byte[count];
            Array.Copy(audioData, offset, fallback, 0, count);
            return fallback;
        }
    }

    private async Task ReceiveMessagesAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64]; // 64KB receive buffer

        try
        {
            while (_webSocket != null && _webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log($"🔌 WebSocket closed by server: {result.CloseStatus} - {result.CloseStatusDescription}");
                    break;
                }

                ms.Seek(0, SeekOrigin.Begin);
                var json = Encoding.UTF8.GetString(ms.ToArray());

                Log($"📨 Raw message received: {json}");
                ProcessResponse(json);
            }
        }
        catch (OperationCanceledException)
        {
            Log("🛑 Receive task cancelled");
            _setupCompletionSource?.TrySetCanceled();
            return;
        }
        catch (Exception ex)
        {
            Log($"❌ Receive error: {ex.Message}");
            _setupCompletionSource?.TrySetException(ex);
        }

        // Connection dropped (session timeout or server close) — reconnect if not shutting down
        if (!ct.IsCancellationRequested)
        {
            Log("🔄 Connection lost, reconnecting...");
            _isConnected = false;
            await ReconnectAsync();
        }
    }

    private void ProcessResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            // Handle setupComplete confirmation
            if (doc.RootElement.TryGetProperty("setupComplete", out _))
            {
                Log("✅ Gemini setup complete, ready to receive audio");
                _setupCompletionSource?.TrySetResult(true);
            }

            // Handle input_transcription (real-time user speech transcription from native-audio model)
            if (doc.RootElement.TryGetProperty("serverContent", out var serverContent))
            {
                // Accumulate verbatim speech chunks; fire OnTurnComplete with the full sentence
                if (serverContent.TryGetProperty("inputTranscription", out var inputTranscription))
                {
                    if (inputTranscription.TryGetProperty("text", out var text))
                    {
                        var chunk = text.GetString();
                        if (!string.IsNullOrWhiteSpace(chunk))
                        {
                            Log($"📝 Input chunk: {chunk}");
                            _lastTranscriptionTime = DateTime.UtcNow;
                            lock (_inputTranscriptBuffer)
                                _inputTranscriptBuffer.Append(chunk);
                            OnInputTranscriptReceived?.Invoke(chunk);
                        }
                    }
                }

                if (serverContent.TryGetProperty("modelTurn", out var modelTurn) &&
                    modelTurn.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                        {
                            var transcript = text.GetString();
                            if (!string.IsNullOrWhiteSpace(transcript))
                            {
                                Log($"📝 Model Turn: {transcript}");
                                _lastTranscriptionTime = DateTime.UtcNow;
                                OnResponseReceived?.Invoke(transcript);
                            }
                        }
                    }
                }

                // Fire full accumulated sentence for question detection, then clear buffer
                if (serverContent.TryGetProperty("turnComplete", out var turnComplete) &&
                    turnComplete.GetBoolean())
                {
                    string fullSentence;
                    lock (_inputTranscriptBuffer)
                    {
                        fullSentence = _inputTranscriptBuffer.ToString().Trim();
                        _inputTranscriptBuffer.Clear();
                    }

                    if (!string.IsNullOrWhiteSpace(fullSentence))
                    {
                        Log($"✅ Turn complete: {fullSentence}");
                        OnTurnComplete?.Invoke(fullSentence);
                    }
                    else
                    {
                        Log("✅ Turn complete (no transcript)");
                    }
                }
            }

            // Handle errors
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var errorMessage = error.GetRawText();
                LastServerError = errorMessage;
                Log($"❌ Gemini error: {errorMessage}");
                _setupCompletionSource?.TrySetException(new InvalidOperationException($"Gemini Live API error: {errorMessage}"));
                _liveConnectFailed = true;
            }

            // Log unknown message types for debugging
            if (!doc.RootElement.TryGetProperty("serverContent", out _) &&
                !doc.RootElement.TryGetProperty("setupComplete", out _) &&
                !doc.RootElement.TryGetProperty("error", out _))
            {
                Log($"ℹ️ Gemini message: {json}");
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Error processing response: {ex.Message}");
            Log($"Raw JSON: {json}");
        }
    }

    public async Task ProcessAudioFileAsync(string filePath)
    {
        if (!_isConnected || _cts == null)
        {
            Console.WriteLine("⚠️ Not connected to Gemini");
            return;
        }

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"❌ File not found: {filePath}");
            return;
        }

        try
        {
            Console.WriteLine($"📂 Processing file: {Path.GetFileName(filePath)}");

            // Read the entire audio file
            var audioBytes = await File.ReadAllBytesAsync(filePath, _cts.Token);
            var base64Audio = Convert.ToBase64String(audioBytes);

            var fileExtension = Path.GetExtension(filePath).ToLower();
            var mimeType = fileExtension switch
            {
                ".wav" => "audio/wav",
                ".mp3" => "audio/mp3",
                ".m4a" => "audio/mp4",
                ".ogg" => "audio/ogg",
                ".flac" => "audio/flac",
                _ => "audio/wav"
            };

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe this audio file. Return only the transcript text. Do not provide a summary." },
                            new { 
                                inline_data = new { 
                                    mime_type = mimeType, 
                                    data = base64Audio
                                } 
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent?key={_apiKey}";

            Console.WriteLine($"⏳ Sending {audioBytes.Length / 1024 / 1024}MB to Gemini...");

            var response = await _httpClient.PostAsync(url, content, _cts.Token);

            if (response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(_cts.Token);
                var parsedResponse = ParseResponse(responseText);

                Console.WriteLine($"\n🤖 Gemini Response:\n{parsedResponse}\n");

                // Optionally save to file
                var outputPath = Path.Combine(
                    Path.GetDirectoryName(filePath) ?? "",
                    $"{Path.GetFileNameWithoutExtension(filePath)}_transcription.txt"
                );
                await File.WriteAllTextAsync(outputPath, parsedResponse, _cts.Token);
                Console.WriteLine($"💾 Transcription saved to: {outputPath}");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(_cts.Token);
                Console.WriteLine($"⚠️ Gemini API error ({response.StatusCode})");

                // Parse error for rate limit info
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(error);
                        if (doc.RootElement.TryGetProperty("error", out var errorObj) &&
                            errorObj.TryGetProperty("message", out var message))
                        {
                            Console.WriteLine($"⏳ {message.GetString()}");
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error processing file: {ex.Message}");
        }
    }

    public async Task StreamAnswerForQuestionAsync(string question, Action<string> onChunk, CancellationToken cancellationToken = default)
    {
        if (!_isConnected || string.IsNullOrWhiteSpace(question))
            return;

        var tokenToUse = cancellationToken == default && _cts != null ? _cts.Token : cancellationToken;

        try
        {
            Log($"❓ Streaming answer for: {question}");

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = $"Answer this question briefly and directly:\n\nQuestion: {question}" }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:streamGenerateContent?alt=sse&key={_apiKey}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, tokenToUse);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(tokenToUse);
                Log($"❌ Gemini streaming QA error ({response.StatusCode}): {error}");
                return;
            }

            using var stream = await response.Content.ReadAsStreamAsync(tokenToUse);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !tokenToUse.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(tokenToUse);
                if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                    continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]")
                    break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                        && candidates.GetArrayLength() > 0
                        && candidates[0].TryGetProperty("content", out var candidateContent)
                        && candidateContent.TryGetProperty("parts", out var parts)
                        && parts.GetArrayLength() > 0
                        && parts[0].TryGetProperty("text", out var text))
                    {
                        var chunk = text.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                            onChunk(chunk);
                    }
                }
                catch { /* skip malformed SSE chunks */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"❌ Error streaming answer: {ex.Message}");
        }
    }

    public async Task<string?> GetAnswerForQuestionAsync(string question, CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
        {
            Log("⚠️ GetAnswerForQuestionAsync called but not connected to Gemini");
            return null;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            Log("⚠️ GetAnswerForQuestionAsync called with empty question");
            return null;
        }

        try
        {
            Log($"❓ Processing question: {question}");

            var tokenToUse = cancellationToken == default && _cts != null
                ? _cts.Token
                : cancellationToken;

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new
                            {
                                text = $"Answer this question briefly and directly:\n\nQuestion: {question}"
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent?key={_apiKey}";

            Log($"🌐 Sending Q&A request to Gemini...");
            var response = await _httpClient.PostAsync(url, content, tokenToUse);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(tokenToUse);
                Log($"❌ Gemini QA API error ({response.StatusCode}): {error}");
                return null;
            }

            var responseText = await response.Content.ReadAsStringAsync(tokenToUse);
            var answer = ParseResponse(responseText);

            Log($"✅ Got answer: {answer}");
            return answer;
        }
        catch (Exception ex)
        {
            Log($"❌ Error getting answer from Gemini: {ex.Message}");
            return null;
        }
    }

    private string ParseResponse(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.GetArrayLength() > 0)
            {
                var firstCandidate = candidates[0];
                if (firstCandidate.TryGetProperty("content", out var content) &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var firstPart = parts[0];
                    if (firstPart.TryGetProperty("text", out var text))
                    {
                        return text.GetString() ?? "No response";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error parsing response: {ex.Message}");
        }

        return jsonResponse;
    }

    public async Task DisconnectAsync()
    {
        _isConnected = false;

        try
        {
            // Cancel operations
            _cts?.Cancel();
            _setupCompletionSource?.TrySetCanceled();

            // Wait for receive task to complete
            if (_receiveTask != null)
            {
                await Task.WhenAny(_receiveTask, Task.Delay(2000));
            }

            // Close WebSocket gracefully
            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client disconnecting",
                    CancellationToken.None
                );
            }

            _webSocket?.Dispose();
            _webSocket = null;

            Console.WriteLine("🔌 Disconnected from Gemini");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error during disconnect: {ex.Message}");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _setupCompletionSource = null;
            _currentWaveFormat = null;
        }
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _httpClient?.Dispose();
    }
}


















