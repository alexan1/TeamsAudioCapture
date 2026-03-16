using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Extensions.Configuration;
using MessageBox = System.Windows.MessageBox;

namespace TeamsAudioCapture;

public partial class SettingsWindow : Window
{
    private readonly IConfiguration _configuration;

    public SettingsWindow(IConfiguration configuration)
    {
        InitializeComponent();
        _configuration = configuration;
        LoadSettings();
    }

    private void LoadSettings()
    {
        PopulateComboBoxFromSectionKeys(TranscriptionProviderComboBox, "Transcription:Providers");
        PopulateComboBoxFromSectionKeys(QAModelComboBox, "QA:Models");

        SelectComboBoxItem(TranscriptionProviderComboBox, _configuration["Transcription:SelectedProvider"]);
        SelectComboBoxItem(QAModelComboBox, _configuration["QA:SelectedModel"]);

        if (TranscriptionProviderComboBox.SelectedItem == null && TranscriptionProviderComboBox.Items.Count > 0)
        {
            TranscriptionProviderComboBox.SelectedIndex = 0;
        }

        if (QAModelComboBox.SelectedItem == null && QAModelComboBox.Items.Count > 0)
        {
            QAModelComboBox.SelectedIndex = 0;
        }

        DeepgramApiKeyTextBox.Text = _configuration["ApiKeys:Deepgram"] ?? string.Empty;
        ApiKeyTextBox.Text = _configuration["ApiKeys:Gemini"] ?? string.Empty;
        OpenAiApiKeyTextBox.Text = _configuration["ApiKeys:OpenAI"] ?? string.Empty;
        ClaudeApiKeyTextBox.Text = _configuration["ApiKeys:Claude"] ?? string.Empty;
        MercuryApiKeyTextBox.Text = _configuration["ApiKeys:Mercury"] ?? string.Empty;

        var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
        var captureMicrophone = _configuration.GetValue<bool>("Recording:CaptureMicrophone", false);
        var processWithLiveApi = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);
        var showTranscript = _configuration.GetValue<bool>("Recording:ShowTranscript", false);
        var saveLocation = _configuration["Recording:AudioSaveLocation"] ?? "";

        SaveAudioCheckBox.IsChecked = saveAudio;
        CaptureMicrophoneCheckBox.IsChecked = captureMicrophone;
        ProcessWithGeminiCheckBox.IsChecked = processWithLiveApi;
        ShowTranscriptCheckBox.IsChecked = showTranscript;
        SaveLocationTextBox.Text = string.IsNullOrWhiteSpace(saveLocation)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : saveLocation;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var deepgramApiKey = DeepgramApiKeyTextBox.Text.Trim();
            var geminiApiKey = ApiKeyTextBox.Text.Trim();
            var openAiApiKey = OpenAiApiKeyTextBox.Text.Trim();
            var claudeApiKey = ClaudeApiKeyTextBox.Text.Trim();
            var mercuryApiKey = MercuryApiKeyTextBox.Text.Trim();
            var saveAudio = SaveAudioCheckBox.IsChecked ?? true;
            var captureMicrophone = CaptureMicrophoneCheckBox.IsChecked ?? false;
            var processWithLiveApi = ProcessWithGeminiCheckBox.IsChecked ?? false;
            var showTranscript = ShowTranscriptCheckBox.IsChecked ?? false;
            var saveLocation = SaveLocationTextBox.Text.Trim();
            var selectedTranscriptionProvider = TranscriptionProviderComboBox.SelectedItem as string ?? string.Empty;
            var selectedQaModel = QAModelComboBox.SelectedItem as string ?? string.Empty;

            if (!saveAudio && !processWithLiveApi)
            {
                MessageBox.Show(Properties.Resources.ValidationRecordingMode,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedTranscriptionProvider) || string.IsNullOrWhiteSpace(selectedQaModel))
            {
                MessageBox.Show("Please select both a transcription provider and a Q/A model.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedProviderType = _configuration[$"Transcription:Providers:{selectedTranscriptionProvider}:Provider"];
            if (processWithLiveApi && string.Equals(selectedProviderType, "OpenAI", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(openAiApiKey))
            {
                MessageBox.Show(Properties.Resources.ValidationOpenAiApiKeyRequired,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (processWithLiveApi && string.Equals(selectedProviderType, "Gemini", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(geminiApiKey))
            {
                MessageBox.Show(Properties.Resources.ValidationGeminiApiKeyRequired,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (processWithLiveApi && string.Equals(selectedProviderType, "Deepgram", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(deepgramApiKey))
            {
                MessageBox.Show("Deepgram API key is required when using Deepgram live transcription.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedQaProvider = _configuration[$"QA:Models:{selectedQaModel}:Provider"];
            var qaApiKeyMissing = selectedQaProvider switch
            {
                "ChatGPT" => string.IsNullOrWhiteSpace(openAiApiKey),
                "Claude" => string.IsNullOrWhiteSpace(claudeApiKey),
                "Mercury" => string.IsNullOrWhiteSpace(mercuryApiKey),
                _ => false
            };

            if (qaApiKeyMissing)
            {
                var providerName = string.IsNullOrWhiteSpace(selectedQaProvider) ? "selected Q/A" : selectedQaProvider;
                MessageBox.Show($"{providerName} API key is required for the selected Q/A model.",
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(saveLocation))
            {
                saveLocation = "";
            }

            var localSettingsPath = LocalSettingsPath.GetPath();
            var localSettingsDirectory = Path.GetDirectoryName(localSettingsPath);
            if (!string.IsNullOrWhiteSpace(localSettingsDirectory))
            {
                Directory.CreateDirectory(localSettingsDirectory);
            }

            var settings = new
            {
                ApiKeys = new
                {
                    Deepgram = deepgramApiKey,
                    Gemini = geminiApiKey,
                    OpenAI = openAiApiKey,
                    Claude = claudeApiKey,
                    Mercury = mercuryApiKey
                },
                Transcription = new
                {
                    SelectedProvider = selectedTranscriptionProvider
                },
                QA = new
                {
                    SelectedModel = selectedQaModel
                },
                Recording = new
                {
                    SaveAudio = saveAudio,
                    ProcessWithGemini = processWithLiveApi,
                    ShowTranscript = showTranscript,
                    AudioSaveLocation = saveLocation,
                    CaptureMicrophone = captureMicrophone,
                    LiveProvider = selectedProviderType
                }
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(localSettingsPath, json);

            MessageBox.Show("Settings saved successfully!", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to save recordings",
            ShowNewFolderButton = true,
            SelectedPath = SaveLocationTextBox.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SaveLocationTextBox.Text = dialog.SelectedPath;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = e.Uri.AbsoluteUri,
            UseShellExecute = true
        });
        e.Handled = true;
    }

    private void PopulateComboBoxFromSectionKeys(System.Windows.Controls.ComboBox comboBox, string sectionPath)
    {
        comboBox.Items.Clear();
        var keys = _configuration.GetSection(sectionPath).GetChildren().Select(c => c.Key);
        foreach (var key in keys)
        {
            comboBox.Items.Add(key);
        }
    }

    private static void SelectComboBoxItem(System.Windows.Controls.ComboBox comboBox, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var item in comboBox.Items)
        {
            if (string.Equals(item as string, value, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }
}
