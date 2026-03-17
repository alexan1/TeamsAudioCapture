using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using TeamsAudioCapture.Services.Factories;
using MessageBox = System.Windows.MessageBox;

namespace TeamsAudioCapture;

public partial class MainWindow : Window
{
    private const string ProviderGemini = "Gemini";
    private const string ProviderOpenAi = "OpenAI";
    private const string ProviderDeepgram = "Deepgram";
    private const string OpenAiProviderOldKey = "OpenAI Realtime API (gpt-4o-realtime-preview) — WebSocket";
    private const string OpenAiProviderCurrentKey = "OpenAI Realtime API (gpt-realtime-1.5) — WebSocket";

    private AudioCapturer? _capturer;
    private ILiveAudioStreamer? _streamer;
    private AnswerWindow? _answerWindow;
    private bool _saveAudio;
    private bool _showTranscript;
    private string _liveProvider = ProviderGemini;
    private readonly HashSet<string> _answeredQuestions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _transcriptLock = new();
    private readonly object _sessionTextLock = new();
    private readonly StringBuilder _sessionTranscript = new();
    private readonly StringBuilder _sessionQna = new();
    private string _lastTranscriptChunk = string.Empty;
    private DispatcherTimer _recordingTimer;
    private DateTime _recordingStartTime;
    private readonly IConfiguration _configuration;
    private readonly IQAServiceFactory _qaServiceFactory;

    public MainWindow(IConfiguration configuration, IQAServiceFactory qaServiceFactory)
    {
        InitializeComponent();
        _configuration = configuration;
        _qaServiceFactory = qaServiceFactory;
        LoadConfiguration();
        
        _recordingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _recordingTimer.Tick += RecordingTimer_Tick;
    }

    private void LoadConfiguration()
    {
        var selectedProviderName = ResolveTranscriptionProviderKey(_configuration["Transcription:SelectedProvider"]);
        var providerType = selectedProviderName is null
            ? null
            : _configuration[$"Transcription:Providers:{selectedProviderName}:Provider"];

        _liveProvider = string.IsNullOrWhiteSpace(providerType)
            ? (_configuration["Recording:LiveProvider"] ?? ProviderGemini)
            : providerType;

        var processWithLiveApi = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);
        var apiKey = _liveProvider switch
        {
            ProviderOpenAi => _configuration["ApiKeys:OpenAI"],
            ProviderDeepgram => _configuration["ApiKeys:Deepgram"],
            _ => _configuration["ApiKeys:Gemini"]
        };

        if (processWithLiveApi && !string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
        {
            GeminiStatusText.Text = "(Ready to connect)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Green;
        }
        else if (processWithLiveApi)
        {
            GeminiStatusText.Text = "(API key required - click Settings)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else
        {
            GeminiStatusText.Text = "(Disabled in Settings)";
            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }

        var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
        _showTranscript = _configuration.GetValue<bool>("Recording:ShowTranscript", false);
        ShowTranscriptRuntimeCheckBox.IsChecked = _showTranscript;
        var mode = (saveAudio, processWithLiveApi) switch
        {
            (true, true) => "ðŸ’¾+ðŸ¤– Save & Process",
            (true, false) => "ðŸ’¾ Save Only",
            (false, true) => "ðŸ¤– Process Only",
            _ => "âš ï¸ Not Configured"
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
                ? Properties.Resources.TranscriptEnabledMessage
                : transcriptSnapshot);
            return;
        }

        SetGeminiResponseText(Properties.Resources.TranscriptHiddenMessage);
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

    private static readonly HashSet<string> _questionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "where", "when", "who", "why", "how",
        "can", "could", "would", "should", "will", "shall",
        "do", "does", "did", "is", "are", "was", "were",
        "have", "has", "had", "may", "might", "must"
    };

    private static string? ExtractQuestion(string transcriptText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return null;
        }

        var lines = transcriptText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length >= 3)
            .ToList();

        if (lines.Count == 0)
        {
            return null;
        }

        // Prefer lines with an explicit "?" mark
        var lineWithMark = lines.LastOrDefault(l => l.Contains('?'));
        if (lineWithMark != null)
        {
            var questionEnd = lineWithMark.LastIndexOf('?');
            var question = lineWithMark[..(questionEnd + 1)].Trim();
            return question.Length < 3 ? null : question;
        }

        // Fallback: detect questions by question words even when "?" is absent
        // (speech-to-text APIs often omit punctuation)
        var lastLine = lines[^1];
        var firstWord = lastLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (_questionWords.Contains(firstWord))
        {
            var normalized = lastLine.TrimEnd('.', ',', ';', '!');
            return normalized + "?";
        }

        return null;
    }

    private async System.Threading.Tasks.Task TryAnswerQuestionAsync(string transcriptChunk)
    {
        var question = ExtractQuestion(transcriptChunk);
        if (string.IsNullOrWhiteSpace(question))
        {
            Console.WriteLine($"ðŸ” No question detected in: {transcriptChunk[..Math.Min(50, transcriptChunk.Length)]}...");
            return;
        }

        Console.WriteLine($"â“ Question detected: {question}");

        if (!_answeredQuestions.Add(question))
        {
            Console.WriteLine($"â­ï¸ Question already answered: {question}");
            return;
        }

        // Show question header immediately â€” no waiting for the answer
        await Dispatcher.InvokeAsync(() =>
        {
            EnsureAnswerWindow();
            _answerWindow?.StartNewAnswer(question);
        });

        var answerBuffer = new StringBuilder();

        try
        {
            if (!TryValidateQaConfiguration(out var qaErrorMessage))
            {
                throw new InvalidOperationException(qaErrorMessage);
            }

            var qaService = _qaServiceFactory.Create(_configuration["QA:SelectedModel"] ?? string.Empty);
            string contextSnapshot;
            lock (_sessionTextLock)
            {
                contextSnapshot = _sessionTranscript.ToString();
            }

            var answer = await qaService.AskAsync(question, contextSnapshot);
            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new InvalidOperationException("The selected Q/A provider returned an empty answer.");
            }

            lock (answerBuffer)
            {
                answerBuffer.Append(answer);
            }

            await Dispatcher.InvokeAsync(() => _answerWindow?.AppendToLastAnswer(answer));
        }
        catch (Exception ex)
        {
            var errorText = $"Unable to answer with the selected Q/A model: {ex.Message}";
            lock (answerBuffer)
            {
                answerBuffer.Append(errorText);
            }

            await Dispatcher.InvokeAsync(() => _answerWindow?.AppendToLastAnswer(errorText));
        }

        await Dispatcher.InvokeAsync(() => _answerWindow?.FinalizeLastAnswer());

        string finalAnswer;
        lock (answerBuffer)
        {
            finalAnswer = answerBuffer.ToString().Trim();
        }

        lock (_sessionTextLock)
        {
            _sessionQna.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Q: {question}");
            _sessionQna.AppendLine($"A: {finalAnswer}");
            _sessionQna.AppendLine();
        }
    }

    private bool TryValidateQaConfiguration(out string errorMessage)
    {
        var selectedQaModel = _configuration["QA:SelectedModel"];
        if (string.IsNullOrWhiteSpace(selectedQaModel))
        {
            errorMessage = "No Q/A model is selected. Please choose one in Settings.";
            return false;
        }

        var qaProvider = _configuration[$"QA:Models:{selectedQaModel}:Provider"];
        var qaModel = _configuration[$"QA:Models:{selectedQaModel}:Model"];
        if (string.IsNullOrWhiteSpace(qaProvider) || string.IsNullOrWhiteSpace(qaModel))
        {
            errorMessage = $"Q/A model '{selectedQaModel}' is not configured correctly.";
            return false;
        }

        var apiKey = qaProvider switch
        {
            "ChatGPT" => _configuration["ApiKeys:OpenAI"],
            "Claude" => _configuration["ApiKeys:Claude"],
            "Mercury" => _configuration["ApiKeys:Mercury"],
            _ => null
        };

        if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_API_KEY_HERE")
        {
            errorMessage = qaProvider switch
            {
                "ChatGPT" => "OpenAI API key is required for the selected Q/A model.",
                "Claude" => "Claude API key is required for the selected Q/A model.",
                "Mercury" => "Mercury API key is required for the selected Q/A model.",
                _ => $"Unsupported Q/A provider '{qaProvider}'."
            };
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private (string transcriptPath, string qnaPath) SaveSessionTextFiles(string audioFilePath)
    {
        var folder = Path.GetDirectoryName(audioFilePath) ?? Directory.GetCurrentDirectory();
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(audioFilePath);
        var transcriptPath = Path.Combine(folder, $"{fileNameWithoutExtension}_transcript.txt");
        var qnaPath = Path.Combine(folder, $"{fileNameWithoutExtension}_qa.txt");

        Directory.CreateDirectory(folder);

        string transcriptText;
        string qnaText;
        lock (_sessionTextLock)
        {
            transcriptText = _sessionTranscript.Length == 0
                ? "No transcript captured during this recording."
                : _sessionTranscript.ToString().Trim();

            qnaText = _sessionQna.Length == 0
                ? "No questions were detected/answered during this recording."
                : _sessionQna.ToString().Trim();
        }

        File.WriteAllText(transcriptPath, transcriptText);
        File.WriteAllText(qnaPath, qnaText);

        return (transcriptPath, qnaPath);
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

    private ILiveAudioStreamer? CreateStreamerForProvider()
    {
        var selectedProviderName = ResolveTranscriptionProviderKey(_configuration["Transcription:SelectedProvider"]);
        if (string.IsNullOrWhiteSpace(selectedProviderName))
        {
            MessageBox.Show(
                "No transcription provider is selected.",
                "Configuration Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var providerRoot = $"Transcription:Providers:{selectedProviderName}";
        var liveProvider = _configuration[$"{providerRoot}:Provider"];

        if (string.IsNullOrWhiteSpace(liveProvider))
        {
            MessageBox.Show(
                $"Selected transcription provider '{selectedProviderName}' is not configured.",
                "Configuration Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"CreateStreamerForProvider: Selected provider = {selectedProviderName}, Provider type = {liveProvider}");

        if (string.Equals(liveProvider, ProviderOpenAi, StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _configuration["ApiKeys:OpenAI"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_API_KEY_HERE")
            {
                MessageBox.Show(
                    Properties.Resources.ValidationOpenAiApiKeyRequired,
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }

            var transcriptionModel = _configuration[$"{providerRoot}:Model"];
            var selectedQaModel = _configuration["QA:SelectedModel"];
            var qnaModel = string.IsNullOrWhiteSpace(selectedQaModel)
                ? null
                : _configuration[$"QA:Models:{selectedQaModel}:Model"];

            if (string.IsNullOrWhiteSpace(transcriptionModel) || string.IsNullOrWhiteSpace(qnaModel))
            {
                MessageBox.Show(
                    "OpenAI transcription or QA model is missing in configuration.",
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"Creating OpenAiRealtimeStreamer with model: {transcriptionModel}");
            return new OpenAiRealtimeStreamer(apiKey, transcriptionModel, qnaModel);
        }

        if (string.Equals(liveProvider, ProviderDeepgram, StringComparison.OrdinalIgnoreCase))
        {
            var apiKey = _configuration["ApiKeys:Deepgram"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_API_KEY_HERE")
            {
                MessageBox.Show(
                    "Deepgram API key is required. Please set it in Settings.",
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }

            var transcriptionModel = _configuration[$"{providerRoot}:Model"];
            var selectedQaModel = _configuration["QA:SelectedModel"];
            var qnaModel = string.IsNullOrWhiteSpace(selectedQaModel)
                ? null
                : _configuration[$"QA:Models:{selectedQaModel}:Model"];

            if (string.IsNullOrWhiteSpace(transcriptionModel) || string.IsNullOrWhiteSpace(qnaModel))
            {
                MessageBox.Show(
                    "Deepgram transcription or QA model is missing in configuration.",
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }

            System.Diagnostics.Debug.WriteLine($"Creating DeepgramAudioStreamer with model: {transcriptionModel}");
            return new DeepgramAudioStreamer(apiKey, transcriptionModel, qnaModel);
        }

        var geminiKey = _configuration["ApiKeys:Gemini"];
        if (string.IsNullOrWhiteSpace(geminiKey) || geminiKey == "YOUR_API_KEY_HERE")
        {
            MessageBox.Show(
                Properties.Resources.ValidationGeminiApiKeyRequired,
                "API Key Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        System.Diagnostics.Debug.WriteLine("Creating GeminiAudioStreamer (default)");
        return new GeminiAudioStreamer(geminiKey);
    }

    private string? ResolveTranscriptionProviderKey(string? configuredProviderKey)
    {
        if (string.IsNullOrWhiteSpace(configuredProviderKey))
        {
            return configuredProviderKey;
        }

        if (!string.Equals(configuredProviderKey, OpenAiProviderOldKey, StringComparison.Ordinal))
        {
            return configuredProviderKey;
        }

        return string.IsNullOrWhiteSpace(_configuration[$"Transcription:Providers:{OpenAiProviderCurrentKey}:Provider"])
            ? configuredProviderKey
            : OpenAiProviderCurrentKey;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
            var processWithLiveApi = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);
            var captureMicrophone = _configuration.GetValue<bool>("Recording:CaptureMicrophone", false);
            var saveLocation = _configuration["Recording:AudioSaveLocation"];
            _saveAudio = saveAudio;
            var selectedProviderName = _configuration["Transcription:SelectedProvider"];
            _liveProvider = string.IsNullOrWhiteSpace(selectedProviderName)
                ? (_configuration["Recording:LiveProvider"] ?? ProviderGemini)
                : (_configuration[$"Transcription:Providers:{selectedProviderName}:Provider"] ?? ProviderGemini);

            if (!saveAudio && !processWithLiveApi)
            {
                MessageBox.Show(
                    Properties.Resources.ValidationRecordingMode,
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (processWithLiveApi && !TryValidateQaConfiguration(out var qaErrorMessage))
            {
                MessageBox.Show(
                    qaErrorMessage,
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (processWithLiveApi)
            {
                _streamer = CreateStreamerForProvider();
                if (_streamer == null)
                {
                    return;
                }

                // Subscribe to events BEFORE connecting
                _streamer.OnResponseReceived += (response) =>
                {
                    if (_showTranscript)
                    {
                        Dispatcher.Invoke(() => AppendGeminiResponseText(response));
                    }
                };

                _streamer.OnInputTranscriptReceived += (chunk) =>
                {
                    lock (_sessionTextLock)
                    {
                        _sessionTranscript.Append(chunk);
                    }

                    if (_showTranscript)
                    {
                        Dispatcher.Invoke(() => AppendGeminiResponseText(chunk));
                    }
                };

                _streamer.OnTurnComplete += (fullSentence) =>
                {
                    _ = TryAnswerQuestionAsync(fullSentence);
                };

                _streamer.OnError += (errorMessage) =>
                {
                    try
                    {
                        if (Dispatcher.CheckAccess())
                        {
                            GeminiStatusText.Text = "(Error)";
                            GeminiStatusText.Foreground = System.Windows.Media.Brushes.Red;
                            SetGeminiResponseText($"⚠️ ERROR\n\n{errorMessage}");
                        }
                        else
                        {
                            Dispatcher.Invoke(() =>
                            {
                                GeminiStatusText.Text = "(Error)";
                                GeminiStatusText.Foreground = System.Windows.Media.Brushes.Red;
                                SetGeminiResponseText($"⚠️ ERROR\n\n{errorMessage}");
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fallback: If UI update fails, at least log it
                        Console.WriteLine($"⚠️ Failed to update UI with error: {errorMessage}");
                        Console.WriteLine($"UI update error: {ex.Message}");
                    }
                };

                try
                {
                    await _streamer.ConnectAsync();

                    using var timeoutCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _streamer.WaitForSetupCompleteAsync(timeoutCts.Token);

                    EnsureAnswerWindow();

                    GeminiStatusText.Text = "(Connected)";
                    GeminiStatusText.Foreground = System.Windows.Media.Brushes.Green;
                }
                catch (Exception ex)
                {
                    var serverError = _streamer?.LastServerError;
                    var logPath = Path.Combine(Path.GetTempPath(), "DeepgramDebug.log");
                    _streamer = null;

                    var errorDetails = ex is TaskCanceledException or OperationCanceledException
                        ? "Setup timed out after 10 seconds. The Live API may be unavailable or slow to respond."
                        : ex.Message;

                    if (!string.IsNullOrWhiteSpace(serverError))
                    {
                        errorDetails = $"Server Error:\n{serverError}\n\nOriginal Exception: {errorDetails}";
                    }

                    errorDetails += $"\n\nDebug log: {logPath}";

                    if (!saveAudio)
                    {
                        MessageBox.Show(
                            $"Failed to connect to Live API.\n\n{errorDetails}\n\nEnable Save Audio in Settings or fix the Live API configuration.",
                            "Live API Connection Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        return;
                    }

                    processWithLiveApi = false;
                    GeminiStatusText.Text = "(Connection failed - recording without Live API)";
                    GeminiStatusText.Foreground = System.Windows.Media.Brushes.Orange;

                    MessageBox.Show(
                        $"Live API connection failed. Recording will continue with audio capture only.\n\n{errorDetails}",
                        "Live API Unavailable",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            _capturer = new AudioCapturer(_streamer, saveLocation, saveAudio, captureMicrophone);
            
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

            var recordingModeText = (saveAudio, processWithLiveApi) switch
            {
                (true, true) => "Save & Process",
                (true, false) => "Save only",
                (false, true) => "Process only (not saving)",
                _ => "Recording"
            };
            
            StatusText.Text = $"ðŸ”´ Recording... {recordingModeText}";
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
            lock (_sessionTextLock)
            {
                _sessionTranscript.Clear();
                _sessionQna.Clear();
            }
            
            SetGeminiResponseText(_showTranscript
                ? Properties.Resources.RecordingStartedMessage
                : Properties.Resources.TranscriptHiddenMessage);
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
                var (transcriptPath, qnaPath) = SaveSessionTextFiles(filePath);
                MessageBox.Show(
                    $"Recording saved!\n\nAudio: {filePath}\nTranscript: {transcriptPath}\nQ&A: {qnaPath}\nSize: {fileInfo.Length / 1024 / 1024} MB", 
                    "Success", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
        }

        if (_streamer != null)
        {
            await _streamer.DisconnectAsync();
            _streamer = null;
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
            if (!TryValidateQaConfiguration(out var qaErrorMessage))
            {
                MessageBox.Show(
                    qaErrorMessage,
                    "Configuration Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_liveProvider == ProviderOpenAi)
            {
                MessageBox.Show(
                    Properties.Resources.OpenAiFileUploadNotSupported,
                    "Not Supported",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_liveProvider == ProviderDeepgram)
            {
                MessageBox.Show(
                    "Deepgram file upload not yet implemented.",
                    "Not Supported",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var apiKey = _configuration["ApiKeys:Gemini"];
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey == "YOUR_API_KEY_HERE")
            {
                MessageBox.Show(
                    Properties.Resources.ValidationGeminiApiKeyRequired,
                    "API Key Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Audio File",
                Filter = "Audio Files (*.mp3;*.wav;*.m4a;*.ogg;*.flac)|*.mp3;*.wav;*.m4a;*.ogg;*.flac|All Files (*.*)|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                var filePath = dialog.FileName;

                StartButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                UploadFileButton.IsEnabled = false;
                SettingsButton.IsEnabled = false;

                StatusText.Text = "⏳ Processing file...";
                StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                SetGeminiResponseText(_showTranscript
                    ? $"Processing: {Path.GetFileName(filePath)}\n\nPlease wait...\n"
                    : Properties.Resources.TranscriptHiddenMessage);

                _streamer = new GeminiAudioStreamer(apiKey);
                await _streamer.ConnectAsync();
                EnsureAnswerWindow();

                _streamer.OnResponseReceived += (response) =>
                {
                    if (_showTranscript)
                    {
                        Dispatcher.Invoke(() => AppendGeminiResponseText(response));
                    }
                };

                _streamer.OnInputTranscriptReceived += (transcript) =>
                {
                    _ = TryAnswerQuestionAsync(transcript);
                };

                _streamer.OnError += (errorMessage) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "❌ File processing failed";
                        StatusText.Foreground = System.Windows.Media.Brushes.Red;
                        SetGeminiResponseText($"⚠️ ERROR\n\n{errorMessage}");
                    });
                };

                _lastTranscriptChunk = string.Empty;

                await _streamer.ProcessAudioFileAsync(filePath);

                await _streamer.DisconnectAsync();

                StatusText.Text = "✅ File processed";
                StatusText.Foreground = System.Windows.Media.Brushes.Green;

                MessageBox.Show(
                    "File processed successfully!\n\nTranscription saved next to the original file.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error processing file: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);

            StatusText.Text = "âŒ Error";
            StatusText.Foreground = System.Windows.Media.Brushes.Red;
        }
        finally
        {
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
        _streamer?.DisconnectAsync().GetAwaiter().GetResult();
    }
}

































































































































































































































































