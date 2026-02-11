using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("🎙️  Teams Audio Capture to Gemini Voice API\n");

        // Load configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();

        var apiKey = configuration["Gemini:ApiKey"];

        GeminiAudioStreamer? geminiStreamer = null;

        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
        {
            geminiStreamer = new GeminiAudioStreamer(apiKey);
            await geminiStreamer.ConnectAsync();
        }
        else
        {
            Console.WriteLine("⚠️ No API key found. Add your key to appsettings.Local.json");
            Console.WriteLine("📝 Recording to WAV only...\n");
        }

        var capturer = new AudioCapturer(geminiStreamer);

        Console.WriteLine("Press 'S' to start recording...");
        while (Console.ReadKey(true).Key != ConsoleKey.S) { }

        capturer.Start();

        Console.WriteLine("Recording... Press 'Q' to stop.\n");
        while (Console.ReadKey(true).Key != ConsoleKey.Q) { }

        await capturer.StopAsync();

        if (geminiStreamer != null)
        {
            await geminiStreamer.DisconnectAsync();
        }

        Console.WriteLine("✅ Recording saved!");
    }
}

public class AudioCapturer
{
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private readonly string _filePath;
    private readonly GeminiAudioStreamer? _geminiStreamer;
    
    public AudioCapturer(GeminiAudioStreamer? geminiStreamer = null)
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"teams-audio-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav"
        );
        _geminiStreamer = geminiStreamer;
    }
    
    public void Start()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            
            Console.WriteLine($"📢 Capturing from: {device.FriendlyName}\n");
            
            _capture = new WasapiLoopbackCapture(device);
            _writer = new WaveFileWriter(_filePath, _capture.WaveFormat);
            
            _capture.DataAvailable += async (s, e) =>
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                Console.WriteLine($"📝 Recording... ({_writer.Length / 1024}KB)");
                
                // Stream to Gemini if connected
                if (_geminiStreamer != null)
                {
                    await _geminiStreamer.StreamAudioAsync(e.Buffer, 0, e.BytesRecorded);
                }
            };
            
            _capture.RecordingStopped += (s, e) =>
            {
                // Cleanup handled in Stop() method
            };
            
            _capture.StartRecording();
            Console.WriteLine("🔴 Recording started...\n");
            
            if (_geminiStreamer != null)
            {
                Console.WriteLine("🌐 Streaming to Gemini Voice API...\n");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }
    }
    
    public async Task StopAsync()
    {
        _capture?.StopRecording();

        // Give time for final audio chunks to process
        await Task.Delay(100);

        _writer?.Dispose();
        _capture?.Dispose();

        Console.WriteLine($"\n✅ Saved to: {_filePath}");
        Console.WriteLine($"📊 File size: {new FileInfo(_filePath).Length / 1024 / 1024}MB");
    }
}
