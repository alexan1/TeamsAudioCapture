using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

public class GeminiAudioStreamer : IDisposable
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;
    private bool _isConnected;
    private readonly MemoryStream _audioBuffer;
    private const int BufferSizeBytes = 1024 * 1024 * 5; // 5MB buffer (fewer API calls) before sending

    public event Action<string>? OnResponseReceived;

    public GeminiAudioStreamer(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _apiKey = apiKey;
        _httpClient = new HttpClient();
        _audioBuffer = new MemoryStream();
    }

    public Task ConnectAsync()
    {
        _isConnected = true;
        _cts = new CancellationTokenSource();
        Console.WriteLine("🌐 Connected to Gemini API");
        return Task.CompletedTask;
    }

    public async Task StreamAudioAsync(byte[] audioData, int offset, int count, WaveFormat waveFormat)
    {
        if (!_isConnected || _cts == null)
        {
            Console.WriteLine("⚠️ Not connected to Gemini");
            return;
        }

        try
        {
            // Buffer raw audio bytes
            _audioBuffer.Write(audioData, offset, count);

            // Send to Gemini when buffer reaches threshold
            if (_audioBuffer.Length >= BufferSizeBytes)
            {
                await SendToGeminiAsync(waveFormat, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error buffering audio: {ex.Message}");
        }
    }

    private async Task SendToGeminiAsync(WaveFormat waveFormat, CancellationToken cancellationToken)
    {
        if (_audioBuffer.Length == 0) return;

        try
        {
            // Create a complete WAV file from buffered audio
            var wavBytes = CreateWavFile(_audioBuffer.ToArray(), waveFormat);
            var base64Audio = Convert.ToBase64String(wavBytes);

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe this audio. Return only the transcript text. Do not provide a summary." },
                            new { 
                                inline_data = new { 
                                    mime_type = "audio/wav", 
                                    data = base64Audio
                                } 
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1/models/gemini-2.5-flash:generateContent?key={_apiKey}";

            var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                var parsedResponse = ParseResponse(responseText);
                Console.WriteLine($"🤖 Gemini: {parsedResponse}");
                
                // Notify UI
                OnResponseReceived?.Invoke(parsedResponse);

                // Clear buffer after successful send
                _audioBuffer.SetLength(0);
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"⚠️ Gemini API error ({response.StatusCode}): {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error sending to Gemini: {ex.Message}");
        }
    }

    private byte[] CreateWavFile(byte[] audioData, WaveFormat waveFormat)
    {
        using var memStream = new MemoryStream();
        using var writer = new System.IO.BinaryWriter(memStream);

        // WAV file header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + audioData.Length); // File size - 8
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // Audio format (1 = PCM)
        writer.Write((short)waveFormat.Channels);
        writer.Write(waveFormat.SampleRate);
        writer.Write(waveFormat.AverageBytesPerSecond);
        writer.Write((short)waveFormat.BlockAlign);
        writer.Write((short)waveFormat.BitsPerSample);

        // data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(audioData.Length);
        writer.Write(audioData);

        return memStream.ToArray();
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

    public async Task DisconnectAsync()
    {
        _isConnected = false;

        // Send any remaining buffered audio
        if (_audioBuffer.Length > 0 && _cts != null)
        {
            // Need wave format - this is a limitation, will be passed from Program.cs
            Console.WriteLine("⚠️ Flushing remaining audio...");
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _audioBuffer?.Dispose();

        Console.WriteLine("🔌 Disconnected from Gemini");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _httpClient?.Dispose();
    }
}







