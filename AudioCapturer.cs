using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.IO;
using System.Threading.Tasks;

namespace TeamsAudioCapture;

public class AudioCapturer
{
    private WasapiLoopbackCapture? _systemCapture;
    private WasapiCapture? _micCapture;
    private WaveFileWriter? _writer;
    private MixingSampleProvider? _mixer;
    private BufferedWaveProvider? _systemBuffer;
    private BufferedWaveProvider? _micBuffer;
    private EventHandler<WaveInEventArgs>? _systemDataHandler;
    private EventHandler<WaveInEventArgs>? _micDataHandler;
    private readonly GeminiAudioStreamer? _geminiStreamer;
    private readonly bool _saveAudio;
    private readonly bool _captureMicrophone;
    private long _totalBytesRecorded;

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
            var systemDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var deviceInfo = $"System: {systemDevice.FriendlyName}";

            _systemCapture = new WasapiLoopbackCapture(systemDevice);
            var waveFormat = _systemCapture.WaveFormat;

            if (_captureMicrophone)
            {
                try
                {
                    var micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    deviceInfo += $" + Mic: {micDevice.FriendlyName}";
                    _micCapture = new WasapiCapture(micDevice);

                    // Convert to common format for mixing
                    var targetFormat = new WaveFormat(waveFormat.SampleRate, waveFormat.BitsPerSample, waveFormat.Channels);
                    _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(targetFormat.SampleRate, targetFormat.Channels));
                    _mixer.ReadFully = true;

                    _systemBuffer = new BufferedWaveProvider(waveFormat);
                    _micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat);

                    var systemSample = _systemBuffer.ToSampleProvider();
                    var micSample = _micBuffer.ToSampleProvider();

                    if (_micCapture.WaveFormat.SampleRate != waveFormat.SampleRate)
                    {
                        micSample = new WdlResamplingSampleProvider(micSample, waveFormat.SampleRate);
                    }

                    _mixer.AddMixerInput(systemSample);
                    _mixer.AddMixerInput(micSample);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke($"Microphone capture failed: {ex.Message}. Continuing with system audio only.");
                    _micCapture = null;
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

                _writer = new WaveFileWriter(FilePath, waveFormat);
            }

            _systemDataHandler = async (s, e) =>
            {
                try
                {
                    if (_captureMicrophone && _mixer != null && _systemBuffer != null)
                    {
                        _systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
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

            if (_micCapture != null && _micBuffer != null)
            {
                _micDataHandler = (s, e) =>
                {
                    try
                    {
                        _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"Microphone error: {ex.Message}");
                    }
                };
                _micCapture.DataAvailable += _micDataHandler;
            }

            if (_captureMicrophone && _mixer != null)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var buffer = new float[waveFormat.SampleRate * waveFormat.Channels];
                        var waveBuffer = new byte[buffer.Length * 4];

                        while (_systemCapture != null && _systemCapture.CaptureState == CaptureState.Capturing)
                        {
                            var samplesRead = _mixer.Read(buffer, 0, buffer.Length);
                            if (samplesRead > 0)
                            {
                                var bytesRead = samplesRead * 4;
                                Buffer.BlockCopy(buffer, 0, waveBuffer, 0, bytesRead);

                                _writer?.Write(waveBuffer, 0, bytesRead);
                                _totalBytesRecorded += bytesRead;
                                OnDataRecorded?.Invoke(_totalBytesRecorded);

                                if (_geminiStreamer != null)
                                {
                                    await _geminiStreamer.StreamAudioAsync(waveBuffer, 0, bytesRead, waveFormat);
                                }
                            }

                            await Task.Delay(10);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke($"Mixer error: {ex.Message}");
                    }
                });
            }

            _systemCapture.RecordingStopped += (s, e) =>
            {
                // Cleanup handled in Stop() method
            };

            _systemCapture.StartRecording();
            _micCapture?.StartRecording();
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
            _mixer = null;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Stop error: {ex.Message}");
        }
    }
}
