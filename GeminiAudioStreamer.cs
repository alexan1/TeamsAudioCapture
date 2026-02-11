using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class GeminiAudioStreamer : IDisposable
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;
    private bool _isConnected;
    private readonly StringBuilder _audioBuffer;
    private const int BufferSize = 1024 * 100; // 100KB buffer before sending

    public GeminiAudioStreamer(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _apiKey = apiKey;
        _httpClient = new HttpClient();
        _audioBuffer = new StringBuilder();
    }

    public Task ConnectAsync()
    {
        _isConnected = true;
        _cts = new CancellationTokenSource();
        Console.WriteLine("🌐 Connected to Gemini API");
        return Task.CompletedTask;
    }

    public async Task StreamAudioAsync(byte[] audioData, int offset, int count)
    {
        if (!_isConnected || _cts == null)
        {
            Console.WriteLine("⚠️ Not connected to Gemini");
            return;
        }

        try
        {
            // Buffer audio chunks
            var audioChunk = new byte[count];
            Array.Copy(audioData, offset, audioChunk, 0, count);
            var base64Audio = Convert.ToBase64String(audioChunk);

            _audioBuffer.Append(base64Audio);

            // Send to Gemini when buffer reaches threshold
            if (_audioBuffer.Length >= BufferSize)
            {
                await SendToGeminiAsync(_cts.Token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error buffering audio: {ex.Message}");
        }
    }

    private async Task SendToGeminiAsync(CancellationToken cancellationToken)
    {
        if (_audioBuffer.Length == 0) return;

        try
        {
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Transcribe this audio and provide a summary." },
                            new { 
                                inline_data = new { 
                                    mime_type = "audio/wav", 
                                    data = _audioBuffer.ToString() 
                                } 
                            }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"https://generativelanguage.googleapis.com/v1/models/gemini-1.5-flash-latest:generateContent?key={_apiKey}";

            var response = await _httpClient.PostAsync(url, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"🤖 Gemini: {ParseResponse(responseText)}");

                // Clear buffer after successful send
                _audioBuffer.Clear();
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

        // Send any remaining buffered audio
        if (_audioBuffer.Length > 0 && _cts != null)
        {
            await SendToGeminiAsync(_cts.Token);
        }

        _cts?.Cancel();
        _cts?.Dispose();

        Console.WriteLine("🔌 Disconnected from Gemini");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _httpClient?.Dispose();
    }
}

