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

public class GeminiAudioStreamer : IDisposable
{
    private const string DefaultLiveModel = "models/gemini-2.0-flash-live-001";
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isConnected;
    private Task? _receiveTask;
    private WaveFormat? _currentWaveFormat;
    private TaskCompletionSource<bool>? _setupCompletionSource;

    public event Action<string>? OnResponseReceived;

    public GeminiAudioStreamer(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    public async Task ConnectAsync()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            _setupCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Connect to Gemini Live API
            var uri = new Uri($"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent?key={_apiKey}");

            Console.WriteLine("🌐 Connecting to Gemini Live API...");
            await _webSocket.ConnectAsync(uri, _cts.Token);

            _isConnected = true;
            Console.WriteLine("✅ Connected to Gemini Live API");

            // Start receiving messages
            _receiveTask = Task.Run(() => ReceiveMessagesAsync(_cts.Token), _cts.Token);

            await SendSetupMessageAsync(_cts.Token);
            Console.WriteLine("⏳ Setup sent; waiting for live responses...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to connect to Gemini: {ex.Message}");
            _isConnected = false;
            throw;
        }
    }

    public async Task StreamAudioAsync(byte[] audioData, int offset, int count, WaveFormat waveFormat)
    {
        if (!_isConnected || _webSocket == null || _cts == null)
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
            Console.WriteLine($"❌ Error streaming audio: {ex.Message}");
        }
    }

    private async Task WaitForSetupCompleteAsync(CancellationToken cancellationToken)
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

        await _setupCompletionSource.Task.WaitAsync(cancellationToken);
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
                        responseModalities = new[] { "TEXT" }
                    },
                    systemInstruction = new
                    {
                        parts = new[]
                        {
                            new { text = "You are transcribing an audio stream. Provide real-time transcription of speech. Return only the transcript text without any additional commentary." }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(setup);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket!.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                ct
            );

            Console.WriteLine("📤 Sent setup message to Gemini");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error sending setup: {ex.Message}");
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
            Console.WriteLine($"⚠️ Error converting audio format: {ex.Message}");
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
                    Console.WriteLine("🔌 WebSocket closed by server");
                    break;
                }

                ms.Seek(0, SeekOrigin.Begin);
                var json = Encoding.UTF8.GetString(ms.ToArray());

                ProcessResponse(json);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("🛑 Receive task cancelled");
            if (_isConnected)
            {
                _setupCompletionSource?.TrySetCanceled();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error receiving messages: {ex.Message}");
            _setupCompletionSource?.TrySetException(ex);
        }
    }

    private void ProcessResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            // Handle serverContent responses (transcription)
            if (doc.RootElement.TryGetProperty("serverContent", out var serverContent))
            {
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
                                Console.WriteLine($"📝 Transcript: {transcript}");
                                OnResponseReceived?.Invoke(transcript);
                            }
                        }
                    }
                }
            }

            // Handle setupComplete confirmation
            if (doc.RootElement.TryGetProperty("setupComplete", out _))
            {
                Console.WriteLine("✅ Gemini setup complete, ready to receive audio");
                _setupCompletionSource?.TrySetResult(true);
            }

            // Handle errors
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var errorMessage = error.GetRawText();
                Console.WriteLine($"❌ Gemini error: {errorMessage}");
                _setupCompletionSource?.TrySetException(new InvalidOperationException($"Gemini Live API error: {errorMessage}"));
            }

            if (!doc.RootElement.TryGetProperty("serverContent", out _) &&
                !doc.RootElement.TryGetProperty("setupComplete", out _) &&
                !doc.RootElement.TryGetProperty("error", out _))
            {
                Console.WriteLine($"ℹ️ Gemini message: {json}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Error processing response: {ex.Message}");
            Console.WriteLine($"Raw JSON: {json}");
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

    public async Task<string?> GetAnswerForQuestionAsync(string question, CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
        {
            Console.WriteLine("⚠️ Not connected to Gemini");
            return null;
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        try
        {
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
                                text = $"Answer this question from a live transcript. Be brief and direct. If the transcript does not provide enough context, say you are not sure.\n\nQuestion: {question}"
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent?key={_apiKey}";
            var response = await _httpClient.PostAsync(url, content, tokenToUse);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(tokenToUse);
                Console.WriteLine($"⚠️ Gemini QA API error ({response.StatusCode}): {error}");
                return null;
            }

            var responseText = await response.Content.ReadAsStringAsync(tokenToUse);
            return ParseResponse(responseText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error getting answer from Gemini: {ex.Message}");
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







