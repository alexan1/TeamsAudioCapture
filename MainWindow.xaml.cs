using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;

namespace TeamsAudioCapture;

public partial class MainWindow : Window
{
    private AudioCapturer? _capturer;
    private GeminiAudioStreamer? _geminiStreamer;
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

        var apiKey = _configuration["Gemini:ApiKey"];
        
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
        {
            GeminiStatusText.Text = "(Ready to connect)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        else
        {
            GeminiStatusText.Text = "(Not configured - click Settings)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Initialize Gemini if API key is configured
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

            // Create and start audio capturer
            _capturer = new AudioCapturer(_geminiStreamer);
            
            _capturer.OnDeviceSelected += (deviceName) =>
            {
                Dispatcher.Invoke(() => DeviceText.Text = deviceName);
            };
            
            _capturer.OnDataRecorded += (bytesRecorded) =>
            {
                Dispatcher.Invoke(() =>
                {
                    FileSizeText.Text = $"{bytesRecorded / 1024} KB";
                    
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
            
            // Update UI
            StatusText.Text = "🔴 Recording...";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SettingsButton.IsEnabled = false;
            
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
            if (File.Exists(filePath))
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _recordingTimer.Stop();
        _capturer?.StopAsync().Wait();
        _geminiStreamer?.DisconnectAsync().Wait();
    }
}
