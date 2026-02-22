using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TeamsAudioCapture;

public class AudioCapturer
{
    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _micCapture;
    private WaveFileWriter? _writer;
    private BufferedWaveProvider? _systemBuffer;
    private BufferedWaveProvider? _micBuffer;
    private WaveFormat? _targetFormat;
    private EventHandler<WaveInEventArgs>? _systemDataHandler;
    private EventHandler<WaveInEventArgs>? _micDataHandler;
    private readonly GeminiAudioStreamer? _geminiStreamer;
    private readonly bool _saveAudio;
    private readonly bool _captureMicrophone;
    private long _totalBytesRecorded;
    private System.Threading.Timer? _mixerTimer;
    private readonly object _mixerLock = new();

    public string FilePath { get; private set; }

    // Events for UI updates
    public event Action<string>? OnDeviceSelected;
    public event Action<long>? OnDataRecorded;
    public event Action<string>? OnError;

    public AudioCapturer(GeminiAudioStreamer? geminiStreamer = null, string? saveLocation = null, bool saveAudio = true, bool captureMicrophone = false)
    {
        _geminiStreamer = geminiStreamer;
        _saveAudio = saveAudio;
        _captureMicrophone = captureMicrophone;
        FilePath = string.Empty;

        if (_saveAudio)
        {
            var folder = string.IsNullOrWhiteSpace(saveLocation)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : saveLocation;

            FilePath = Path.Combine(
                folder,
                $"teams-audio-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav"
            );
        }

        _totalBytesRecorded = 0;
    }

    public void Start()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            var systemDevice = GetDefaultAudioEndpointWithFallback(
                enumerator,
                DataFlow.Render,
                "system audio output",
                Role.Multimedia,
                Role.Console,
                Role.Communications
            );

            var deviceInfo = $"System: {systemDevice.FriendlyName}";

            _systemCapture = new WasapiLoopbackCapture(systemDevice);
            var waveFormat = _systemCapture.WaveFormat;

            if (_captureMicrophone)
            {
                try
                {
                    var micDevice = GetDefaultAudioEndpointWithFallback(
                        enumerator,
                        DataFlow.Capture,
                        "microphone input",
                        Role.Communications,
                        Role.Multimedia,
                        Role.Console
                    );
                    deviceInfo += $" + Mic: {micDevice.FriendlyName}";
                    _micCapture = new WasapiCapture(micDevice);

                    // Use common PCM format for mixing
                    _targetFormat = new WaveFormat(waveFormat.SampleRate, 16, waveFormat.Channels);

                    // Create larger buffers to prevent underruns
                    _systemBuffer = new BufferedWaveProvider(_targetFormat)
                    {
                        BufferLength = _targetFormat.AverageBytesPerSecond * 5,
                        DiscardOnBufferOverflow = true
                    };

                    _micBuffer = new BufferedWaveProvider(_targetFormat)
                    {
                        BufferLength = _targetFormat.AverageBytesPerSecond * 5,
                        DiscardOnBufferOverflow = true
                    };
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Microphone capture unavailable: {ex.Message}. Continuing with system audio only.");
                    _micCapture?.Dispose();
                    _micCapture = null;
                    _systemBuffer = null;
                    _micBuffer = null;
                    _targetFormat = null;
                }
            }

            OnDeviceSelected?.Invoke(deviceInfo);

            if (_saveAudio)
            {
                var outputDirectory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var outputFormat = _targetFormat ?? waveFormat;
                _writer = new WaveFileWriter(FilePath, outputFormat);
            }

            _systemDataHandler = async (s, e) =>
            {
                try
                {
                    if (_captureMicrophone && _systemBuffer != null && _targetFormat != null)
                    {
                        // Convert and add to buffer
                        var converted = ConvertAudioFormat(e.Buffer, e.BytesRecorded, waveFormat, _targetFormat);
                        _systemBuffer.AddSamples(converted, 0, converted.Length);
                    }
                    else
                    {
                        _writer?.Write(e.Buffer, 0, e.BytesRecorded);
                        _totalBytesRecorded += e.BytesRecorded;
                        OnDataRecorded?.Invoke(_totalBytesRecorded);

                        if (_geminiStreamer != null)
                        {
                            await _geminiStreamer.StreamAudioAsync(e.Buffer, 0, e.BytesRecorded, waveFormat);
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"System audio error: {ex.Message}");
                }
            };
            _systemCapture.DataAvailable += _systemDataHandler;

            if (_micCapture != null && _micBuffer != null && _targetFormat != null)
            {
                _micDataHandler = (s, e) =>
                {
                    try
                    {
                        // Convert and add to buffer
                        var converted = ConvertAudioFormat(e.Buffer, e.BytesRecorded, _micCapture.WaveFormat, _targetFormat);
                        _micBuffer.AddSamples(converted, 0, converted.Length);
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"Microphone error: {ex.Message}");
                    }
                };
                _micCapture.DataAvailable += _micDataHandler;
            }

            _systemCapture.RecordingStopped += (s, e) =>
            {
                // Cleanup handled in Stop() method
            };

            _systemCapture.StartRecording();
            _micCapture?.StartRecording();

            // Start mixer timer if microphone is enabled
            if (_captureMicrophone && _systemBuffer != null && _micBuffer != null && _targetFormat != null)
            {
                _mixerTimer = new System.Threading.Timer(MixAudio, null, 50, 20); // Mix every 20ms
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
            throw;
        }
    }

    private static MMDevice GetDefaultAudioEndpointWithFallback(MMDeviceEnumerator enumerator, DataFlow dataFlow, string endpointDescription, params Role[] roles)
    {
        Exception? lastException = null;

        foreach (var role in roles)
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(dataFlow, role);
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        var roleList = string.Join(", ", roles);
        throw new InvalidOperationException(
            $"No default {endpointDescription} device is available for roles [{roleList}].", 
            lastException
        );
    }

    private byte[] ConvertAudioFormat(byte[] input, int length, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        using var sourceStream = new RawSourceWaveStream(input, 0, length, sourceFormat);
        var sampleProvider = sourceStream.ToSampleProvider();

        // Resample if needed
        if (sourceFormat.SampleRate != targetFormat.SampleRate)
        {
            sampleProvider = new WdlResamplingSampleProvider(sampleProvider, targetFormat.SampleRate);
        }

        // Convert channels if needed
        if (sampleProvider.WaveFormat.Channels == 1 && targetFormat.Channels == 2)
        {
            sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
        }
        else if (sampleProvider.WaveFormat.Channels == 2 && targetFormat.Channels == 1)
        {
            sampleProvider = sampleProvider.ToMono();
        }

        // Convert to target format
        var converted = new SampleToWaveProvider16(sampleProvider);
        var outputBuffer = new byte[length * 2]; // Rough estimate
        int bytesRead = converted.Read(outputBuffer, 0, outputBuffer.Length);

        var result = new byte[bytesRead];
        Array.Copy(outputBuffer, result, bytesRead);
        return result;
    }

    private void MixAudio(object? state)
    {
        if (_systemBuffer == null || _micBuffer == null || _writer == null || _targetFormat == null)
            return;

        lock (_mixerLock)
        {
            try
            {
                // Calculate how much data we can read
                var available = Math.Min(_systemBuffer.BufferedBytes, _micBuffer.BufferedBytes);
                if (available < _targetFormat.AverageBytesPerSecond / 50) // At least 20ms
                    return;

                var chunkSize = Math.Min(available, _targetFormat.AverageBytesPerSecond / 20); // Max 50ms chunks
                var systemData = new byte[chunkSize];
                var micData = new byte[chunkSize];
                var mixedData = new byte[chunkSize];

                _systemBuffer.Read(systemData, 0, chunkSize);
                _micBuffer.Read(micData, 0, chunkSize);

                // Mix audio by averaging samples
                for (int i = 0; i < chunkSize; i += 2)
                {
                    if (i + 1 >= chunkSize) break;

                    short systemSample = BitConverter.ToInt16(systemData, i);
                    short micSample = BitConverter.ToInt16(micData, i);

                    // Mix with proper clamping
                    int mixed = systemSample + micSample;
                    mixed = Math.Clamp(mixed, short.MinValue, short.MaxValue);

                    var mixedBytes = BitConverter.GetBytes((short)mixed);
                    mixedData[i] = mixedBytes[0];
                    mixedData[i + 1] = mixedBytes[1];
                }

                _writer.Write(mixedData, 0, chunkSize);
                _totalBytesRecorded += chunkSize;
                OnDataRecorded?.Invoke(_totalBytesRecorded);

                if (_geminiStreamer != null)
                {
                    _ = _geminiStreamer.StreamAudioAsync(mixedData, 0, chunkSize, _targetFormat);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Mixer error: {ex.Message}");
            }
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _mixerTimer?.Dispose();
            _mixerTimer = null;

            if (_systemCapture != null && _systemDataHandler != null)
            {
                _systemCapture.DataAvailable -= _systemDataHandler;
                _systemDataHandler = null;
            }

            if (_micCapture != null && _micDataHandler != null)
            {
                _micCapture.DataAvailable -= _micDataHandler;
                _micDataHandler = null;
            }

            _systemCapture?.StopRecording();
            _micCapture?.StopRecording();

            await Task.Delay(100).ConfigureAwait(false);

            _writer?.Dispose();
            _systemCapture?.Dispose();
            _micCapture?.Dispose();

            _writer = null;
            _systemCapture = null;
            _micCapture = null;
            _systemBuffer = null;
            _micBuffer = null;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Stop error: {ex.Message}");
        }
    }
}
