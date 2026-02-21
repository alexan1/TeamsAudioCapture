using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using MessageBox = System.Windows.MessageBox;

namespace TeamsAudioCapture;

public partial class MainWindow : Window
{
    private AudioCapturer? _capturer;
    private GeminiAudioStreamer? _geminiStreamer;
    private bool _saveAudio;
    private DispatcherTimer _recordingTimer;
    private DateTime _recordingStartTime;
    private IConfiguration _configuration = null!;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfiguration();
        
        _recordingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _recordingTimer.Tick += RecordingTimer_Tick;
    }

    private void LoadConfiguration()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .Build();

        // Check Gemini API configuration
        var apiKey = _configuration["Gemini:ApiKey"];
        var processWithGemini = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);

        if (processWithGemini && !string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
        {
            GeminiStatusText.Text = "(Ready to connect)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        else if (processWithGemini)
        {
            GeminiStatusText.Text = "(API key required - click Settings)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else
        {
            GeminiStatusText.Text = "(Disabled in Settings)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }

        // Update status based on recording mode
        var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
        var mode = (saveAudio, processWithGemini) switch
        {
            (true, true) => "💾+🤖 Save & Process",
            (true, false) => "💾 Save Only",
            (false, true) => "🤖 Process Only",
            _ => "⚠️ Not Configured"
        };

        Title = $"Teams Audio Capture - {mode}";
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Load settings
            var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
            var processWithGemini = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);
            var saveLocation = _configuration["Recording:AudioSaveLocation"];
            _saveAudio = saveAudio;

            // Validate settings
            if (!saveAudio && !processWithGemini)
            {
                MessageBox.Show(
                    "Both 'Save Audio' and 'Process with Gemini' are disabled.\nPlease enable at least one option in Settings.", 
                    "Configuration Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
                return;
            }

            // Initialize Gemini if enabled and configured
            if (processWithGemini)
            {
                var apiKey = _configuration["Gemini:ApiKey"];
                if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
                {
                    _geminiStreamer = new GeminiAudioStreamer(apiKey);
                    await _geminiStreamer.ConnectAsync();

                    _geminiStreamer.OnResponseReceived += (response) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            GeminiResponseText.Text += $"\n[{DateTime.Now:HH:mm:ss}] {response}\n";
                        });
                    };

                    GeminiStatusText.Text = "(Connected)";
                    GeminiStatusText.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    MessageBox.Show(
                        "Gemini processing is enabled but API key is not configured.\nPlease add your API key in Settings.", 
                        "API Key Required", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Warning);
                    return;
                }
            }

            // Create and start audio capturer
            _capturer = new AudioCapturer(_geminiStreamer, saveLocation, saveAudio);
            
            _capturer.OnDeviceSelected += (deviceName) =>
            {
                Dispatcher.Invoke(() => DeviceText.Text = deviceName);
            };
            
            _capturer.OnDataRecorded += (bytesRecorded) =>
            {
                Dispatcher.Invoke(() =>
                {
                    FileSizeText.Text = _saveAudio
                        ? $"{bytesRecorded / 1024} KB"
                        : "N/A (not saving)";
                    
                    // Simulate audio level (you can enhance this with actual audio analysis)
                    AudioLevelBar.Value = Math.Min(100, (bytesRecorded % 1000) / 10);
                });
            };
            
            _capturer.OnError += async (error) =>
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    MessageBox.Show($"Error: {error}", "Recording Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    await StopRecording();
                });
            };

            _capturer.Start();

            var recordingModeText = (saveAudio, processWithGemini) switch
            {
                (true, true) => "Save & Process",
                (true, false) => "Save only",
                (false, true) => "Process only (not saving)",
                _ => "Recording"
            };
            
            // Update UI
            StatusText.Text = $"🔴 Recording... {recordingModeText}";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            UploadFileButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            FileSizeText.Text = _saveAudio ? "0 KB" : "N/A (not saving)";
            
            _recordingStartTime = DateTime.Now;
            _recordingTimer.Start();
            
            GeminiResponseText.Text = "Recording started...\n";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to start recording: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopRecording();
    }

    private async System.Threading.Tasks.Task StopRecording()
    {
        if (_capturer != null)
        {
            await _capturer.StopAsync();
            
            var filePath = _capturer.FilePath;
            if (_saveAudio && !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                MessageBox.Show(
                    $"Recording saved!\n\nFile: {filePath}\nSize: {fileInfo.Length / 1024 / 1024} MB", 
                    "Success", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
        }

        if (_geminiStreamer != null)
        {
            await _geminiStreamer.DisconnectAsync();
            GeminiStatusText.Text = "(Disconnected)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }

        _recordingTimer.Stop();
        
        // Reset UI
        StatusText.Text = "Ready";
        StatusText.Foreground = System.Windows.Media.Brushes.Green;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        UploadFileButton.IsEnabled = true;
        SettingsButton.IsEnabled = true;
        AudioLevelBar.Value = 0;
    }

    private void RecordingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - _recordingStartTime;
        RecordingTimeText.Text = elapsed.ToString(@"hh\:mm\:ss");
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow(_configuration);
        if (settingsWindow.ShowDialog() == true)
        {
            LoadConfiguration();
        }
    }

    private async void UploadFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Check if API key is configured
            var apiKey = _configuration["Gemini:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_API_KEY_HERE")
            {
                MessageBox.Show(
                    "Please configure your Gemini API key in Settings first.", 
                    "API Key Required", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Warning);
                return;
            }

            // Open file dialog
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files (*.wav;*.mp3;*.m4a;*.ogg;*.flac)|*.wav;*.mp3;*.m4a;*.ogg;*.flac|All Files (*.*)|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                var filePath = dialog.FileName;

                // Disable buttons during processing
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                UploadFileButton.IsEnabled = false;
                SettingsButton.IsEnabled = false;

                StatusText.Text = "⏳ Processing file...";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                GeminiResponseText.Text = $"Processing: {Path.GetFileName(filePath)}\n\nPlease wait...\n";

                // Initialize Gemini streamer
                _geminiStreamer = new GeminiAudioStreamer(apiKey);
                await _geminiStreamer.ConnectAsync();

                _geminiStreamer.OnResponseReceived += (response) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        GeminiResponseText.Text += $"\n{response}\n";
                    });
                };

                // Process the file
                await _geminiStreamer.ProcessAudioFileAsync(filePath);

                await _geminiStreamer.DisconnectAsync();

                StatusText.Text = "✅ File processed";
                StatusText.Foreground = System.Windows.Media.Brushes.Green;

                MessageBox.Show(
                    $"File processed successfully!\n\nTranscription saved next to the original file.", 
                    "Success", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error processing file: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);

            StatusText.Text = "❌ Error";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
            // Re-enable buttons
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            UploadFileButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _recordingTimer.Stop();
        _capturer?.StopAsync().Wait();
        _geminiStreamer?.DisconnectAsync().Wait();
    }
}
