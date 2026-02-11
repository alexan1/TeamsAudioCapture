using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class GeminiAudioStreamer : IDisposable
{
    private readonly string _apiKey;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private bool _isConnected;

    public GeminiAudioStreamer(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _apiKey = apiKey;
    }

    public async Task ConnectAsync()
    {
        try
        {
            _webSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            var uri = new Uri($"wss://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash-exp:streamGenerateContent?key={_apiKey}");
            
            await _webSocket.ConnectAsync(uri, _cts.Token);
            _isConnected = true;

            Console.WriteLine("🌐 Connected to Gemini Voice API");

            // Start receiving responses
            _ = Task.Run(() => ReceiveResponsesAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Failed to connect to Gemini: {ex.Message}");
            throw;
        }
    }

    public async Task StreamAudioAsync(byte[] audioData, int offset, int count)
    {
        if (!_isConnected || _webSocket == null || _cts == null)
        {
            Console.WriteLine("⚠️ Not connected to Gemini");
            return;
        }

        try
        {
            // Convert audio to base64
            var audioChunk = new byte[count];
            Array.Copy(audioData, offset, audioChunk, 0, count);
            var base64Audio = Convert.ToBase64String(audioChunk);

            // Create JSON payload for Gemini
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new { inline_data = new { mime_type = "audio/wav", data = base64Audio } }
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                _cts.Token
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error streaming to Gemini: {ex.Message}");
        }
    }

    private async Task ReceiveResponsesAsync(CancellationToken cancellationToken)
    {
        if (_webSocket == null) return;

        var buffer = new byte[4096];

        try
        {
            while (_isConnected && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        cancellationToken
                    );
                    _isConnected = false;
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"🤖 Gemini response: {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error receiving from Gemini: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        _isConnected = false;

        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Client closing",
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error closing WebSocket: {ex.Message}");
            }
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();

        Console.WriteLine("🔌 Disconnected from Gemini");
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }
}
