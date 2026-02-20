using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TeamsAudioCapture;

public class AudioCapturer
{
    private WasapiLoopbackCapture? _capture;
    private WaveFileWriter? _writer;
    private readonly GeminiAudioStreamer? _geminiStreamer;
    private long _totalBytesRecorded;

    public string FilePath { get; private set; }

    // Events for UI updates
    public event Action<string>? OnDeviceSelected;
    public event Action<long>? OnDataRecorded;
    public event Action<string>? OnError;

    public AudioCapturer(GeminiAudioStreamer? geminiStreamer = null)
    {
        FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"teams-audio-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav"
        );
        _geminiStreamer = geminiStreamer;
        _totalBytesRecorded = 0;
    }

    public void Start()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            OnDeviceSelected?.Invoke(device.FriendlyName);

            _capture = new WasapiLoopbackCapture(device);
            _writer = new WaveFileWriter(FilePath, _capture.WaveFormat);

            _capture.DataAvailable += async (s, e) =>
            {
                try
                {
                    _writer.Write(e.Buffer, 0, e.BytesRecorded);
                    _totalBytesRecorded += e.BytesRecorded;
                    
                    OnDataRecorded?.Invoke(_totalBytesRecorded);

                    // Stream to Gemini if connected
                    if (_geminiStreamer != null)
                    {
                        await _geminiStreamer.StreamAudioAsync(e.Buffer, 0, e.BytesRecorded, _capture.WaveFormat);
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Data recording error: {ex.Message}");
                }
            };

            _capture.RecordingStopped += (s, e) =>
            {
                // Cleanup handled in Stop() method
            };

            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
            throw;
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _capture?.StopRecording();

            // Give time for final audio chunks to process
            await Task.Delay(100);

            _writer?.Dispose();
            _capture?.Dispose();
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Stop error: {ex.Message}");
        }
    }
}
