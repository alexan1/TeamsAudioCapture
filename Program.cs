using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("🎙️  Teams Audio Capture to WAV File\n");
        
        var capturer = new AudioCapturer();
        
        Console.WriteLine("Press 'S' to start recording...");
        while (Console.ReadKey(true).Key != ConsoleKey.S) { }
        
        capturer.Start();
        
        Console.WriteLine("Recording... Press 'Q' to stop.\n");
        while (Console.ReadKey(true).Key != ConsoleKey.Q) { }
        
        capturer.Stop();
        Console.WriteLine("✅ Recording saved!");
    }
}

public class AudioCapturer
{
    private WasapiLoopbackCapture _capture;
    private WaveFileWriter _writer;
    private string _filePath;
    
    public AudioCapturer()
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"teams-audio-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav"
        );
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
            
            _capture.DataAvailable += (s, e) =>
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                Console.WriteLine($"📝 Recording... ({_writer.Length / 1024}KB)");
            };
            
            _capture.RecordingStopped += (s, e) =>
            {
                // Cleanup handled in Stop() method
            };
            
            _capture.StartRecording();
            Console.WriteLine("🔴 Recording started...\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }
    }
    
    public void Stop()
    {
        _capture?.StopRecording();

        _writer?.Dispose();
        _capture?.Dispose();

        Console.WriteLine($"\n✅ Saved to: {_filePath}");
        Console.WriteLine($"📊 File size: {new FileInfo(_filePath).Length / 1024 / 1024}MB");
    }
}
