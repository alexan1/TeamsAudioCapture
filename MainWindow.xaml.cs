using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using MessageBox = System.Windows.MessageBox;

namespace TeamsAudioCapture;

public partial class MainWindow : Window
{
    private AudioCapturer? _capturer;
    private GeminiAudioStreamer? _geminiStreamer;
    private AnswerWindow? _answerWindow;
    private bool _saveAudio;
    private bool _showTranscript;
    private readonly HashSet<string> _answeredQuestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _transcriptLock = new();
    private string _lastTranscriptChunk = string.Empty;
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
        _showTranscript = _configuration.GetValue<bool>("Recording:ShowTranscript", false);
        ShowTranscriptRuntimeCheckBox.IsChecked = _showTranscript;
        var mode = (saveAudio, processWithGemini) switch
        {
            (true, true) => "💾+🤖 Save & Process",
            (true, false) => "💾 Save Only",
            (false, true) => "🤖 Process Only",
            _ => "⚠️ Not Configured"
        };

        Title = $"Teams Audio Capture - {mode}";
        UpdateTranscriptDisplayMode();
    }

    private void UpdateTranscriptDisplayMode()
    {
        if (_showTranscript)
        {
            string transcriptSnapshot;
            lock (_transcriptLock)
            {
                transcriptSnapshot = _lastTranscriptChunk;
            }

            SetGeminiResponseText(string.IsNullOrWhiteSpace(transcriptSnapshot)
                ? "Transcript enabled. Waiting for speech...\n"
                : transcriptSnapshot);
            return;
        }

        SetGeminiResponseText("Transcript view is hidden. Detected questions and Gemini answers appear in the Gemini Answers window.\n");
    }

    private void SetGeminiResponseText(string text)
    {
        GeminiResponseText.Text = text;
        ScrollTranscriptToEnd();
    }

    private void AppendGeminiResponseText(string text)
    {
        GeminiResponseText.Text += text;
        ScrollTranscriptToEnd();
    }

    private void ScrollTranscriptToEnd()
    {
        Dispatcher.BeginInvoke(() =>
        {
            GeminiResponseText.UpdateLayout();
            GeminiResponseScrollViewer.UpdateLayout();
            GeminiResponseScrollViewer.ScrollToBottom();
        }, DispatcherPriority.Render);
    }

    private string? GetTranscriptDelta(string transcriptChunk)
    {
        var normalized = transcriptChunk.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        lock (_transcriptLock)
        {
            if (string.IsNullOrWhiteSpace(_lastTranscriptChunk))
            {
                _lastTranscriptChunk = normalized;
                return normalized;
            }

            if (string.Equals(_lastTranscriptChunk, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (normalized.StartsWith(_lastTranscriptChunk, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = normalized[_lastTranscriptChunk.Length..];
                _lastTranscriptChunk = normalized;
                return string.IsNullOrWhiteSpace(suffix) ? null : suffix;
            }

            if (_lastTranscriptChunk.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var maxOverlap = Math.Min(_lastTranscriptChunk.Length, normalized.Length);
            var overlapLength = 0;

            for (var i = maxOverlap; i > 0; i--)
            {
                if (_lastTranscriptChunk.EndsWith(normalized[..i], StringComparison.OrdinalIgnoreCase))
                {
                    overlapLength = i;
                    break;
                }
            }

            if (overlapLength > 0)
            {
                var suffix = normalized[overlapLength..];
                _lastTranscriptChunk += suffix;
                return string.IsNullOrWhiteSpace(suffix) ? null : suffix;
            }

            if (_lastTranscriptChunk.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _lastTranscriptChunk += Environment.NewLine + normalized;
            return Environment.NewLine + normalized;
        }
    }

    private static string? ExtractQuestion(string transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return null;
        }

        var lines = transcriptText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Contains('?'))
            .ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        var candidate = lines[^1];
        var questionEnd = candidate.LastIndexOf('?');
        if (questionEnd < 0)
        {
            return null;
        }

        var question = candidate[..(questionEnd + 1)].Trim();
        return question.Length < 3 ? null : question;
    }

    private async System.Threading.Tasks.Task TryAnswerQuestionAsync(string transcriptChunk)
    {
        if (_geminiStreamer == null)
        {
            return;
        }

        var question = ExtractQuestion(transcriptChunk);
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }

        if (!_answeredQuestions.Add(question))
        {
            return;
        }

        var answer = await _geminiStreamer.GetAnswerForQuestionAsync(question);
        if (string.IsNullOrWhiteSpace(answer))
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            EnsureAnswerWindow();
            _answerWindow?.AppendAnswer(question, answer);
        });
    }

    private void EnsureAnswerWindow()
    {
        if (_answerWindow != null)
        {
            if (!_answerWindow.IsVisible)
            {
                _answerWindow.Show();
            }

            if (_answerWindow.WindowState == WindowState.Minimized)
            {
                _answerWindow.WindowState = WindowState.Normal;
            }

            return;
        }

        _answerWindow = new AnswerWindow
        {
            Owner = this
        };

        _answerWindow.Closed += (_, _) =>
        {
            _answerWindow = null;
        };

        _answerWindow.Show();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Load settings
            var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
            var processWithGemini = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);
            var captureMicrophone = _configuration.GetValue<bool>("Recording:CaptureMicrophone", false);
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

                    try
                    {
                        await _geminiStreamer.ConnectAsync();

                        // CRITICAL: Wait for Gemini setup to complete before starting audio capture
                        // Use timeout to prevent indefinite hanging
                        using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _geminiStreamer.WaitForSetupCompleteAsync(timeoutCts.Token);

                        EnsureAnswerWindow();

                        _geminiStreamer.OnResponseReceived += (response) =>
                        {
                            var delta = GetTranscriptDelta(response);
                            if (string.IsNullOrWhiteSpace(delta))
                            {
                                return;
                            }

                            if (_showTranscript)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    AppendGeminiResponseText(delta);
                                });
                            }

                            _ = TryAnswerQuestionAsync(response);
                        };

                        GeminiStatusText.Text = "(Connected)";
                        GeminiStatusText.Foreground = System.Windows.Media.Brushes.Green;
                    }
                    catch (Exception ex)
                    {
                        _geminiStreamer = null;

                        var errorDetails = ex is TaskCanceledException or OperationCanceledException
                            ? "Setup timed out after 10 seconds. The Gemini API may be unavailable or slow to respond."
                            : ex.Message;

                        if (!saveAudio)
                        {
                            MessageBox.Show(
                                $"Failed to connect to Gemini Live API.\n\n{errorDetails}\n\nEnable Save Audio in Settings or fix Gemini configuration.",
                                "Gemini Connection Failed",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                            return;
                        }

                        processWithGemini = false;
                        GeminiStatusText.Text = "(Connection failed - recording without Gemini)";
                        GeminiStatusText.Foreground = System.Windows.Media.Brushes.Orange;

                        MessageBox.Show(
                            $"Gemini Live API connection failed. Recording will continue with audio capture only.\n\n{errorDetails}",
                            "Gemini Unavailable",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
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
            _capturer = new AudioCapturer(_geminiStreamer, saveLocation, saveAudio, captureMicrophone);
            
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
            _answeredQuestions.Clear();
            _lastTranscriptChunk = string.Empty;
            
            SetGeminiResponseText(_showTranscript
                ? "Recording started...\n"
                : "Transcript view is hidden. Detected questions and Gemini answers appear in the Gemini Answers window.\n");
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

    private void ShowTranscriptRuntimeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _showTranscript = ShowTranscriptRuntimeCheckBox.IsChecked == true;
        UpdateTranscriptDisplayMode();
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
                SetGeminiResponseText(_showTranscript
                    ? $"Processing: {Path.GetFileName(filePath)}\n\nPlease wait...\n"
                    : "Transcript view is hidden. Detected questions and Gemini answers appear in the Gemini Answers window.\n");

                // Initialize Gemini streamer
                _geminiStreamer = new GeminiAudioStreamer(apiKey);
                await _geminiStreamer.ConnectAsync();
                EnsureAnswerWindow();

                _geminiStreamer.OnResponseReceived += (response) =>
                {
                    var delta = GetTranscriptDelta(response);
                    if (string.IsNullOrWhiteSpace(delta))
                    {
                        return;
                    }

                    if (_showTranscript)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            AppendGeminiResponseText(delta);
                        });
                    }

                    _ = TryAnswerQuestionAsync(response);
                };
                _lastTranscriptChunk = string.Empty;
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
        _answerWindow?.Close();
        base.OnClosed(e);
        _recordingTimer.Stop();
        _capturer?.StopAsync().GetAwaiter().GetResult();
        _geminiStreamer?.DisconnectAsync().GetAwaiter().GetResult();
    }
}
