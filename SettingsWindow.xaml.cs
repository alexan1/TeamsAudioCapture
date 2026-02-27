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
    private const string ProviderGemini = "Gemini";
    private const string ProviderOpenAi = "OpenAI";
    private const string DefaultOpenAiTranscriptionModel = "gpt-4o-mini-realtime-preview";
    private const string DefaultOpenAiQnaModel = "gpt-4o-mini";

    private readonly IConfiguration _configuration;
    private const string LocalSettingsFile = "appsettings.Local.json";

    public SettingsWindow(IConfiguration configuration)
    {
        InitializeComponent();
        _configuration = configuration;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var geminiApiKey = _configuration["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(geminiApiKey) && geminiApiKey != "YOUR_API_KEY_HERE")
        {
            ApiKeyTextBox.Text = geminiApiKey;
        }

        var openAiApiKey = _configuration["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(openAiApiKey) && openAiApiKey != "YOUR_API_KEY_HERE")
        {
            OpenAiApiKeyTextBox.Text = openAiApiKey;
        }

        var openAiTranscriptionModel = _configuration["OpenAI:TranscriptionModel"];
        if (string.IsNullOrWhiteSpace(openAiTranscriptionModel))
        {
            openAiTranscriptionModel = _configuration["OpenAI:Model"];
        }

        OpenAiTranscriptionModelTextBox.Text = string.IsNullOrWhiteSpace(openAiTranscriptionModel)
            ? DefaultOpenAiTranscriptionModel
            : openAiTranscriptionModel;

        var openAiQnaModel = _configuration["OpenAI:QnaModel"];
        OpenAiQnaModelTextBox.Text = string.IsNullOrWhiteSpace(openAiQnaModel)
            ? DefaultOpenAiQnaModel
            : openAiQnaModel;

        var liveProvider = _configuration["Recording:LiveProvider"] ?? ProviderGemini;
        foreach (var item in LiveProviderComboBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, liveProvider, StringComparison.OrdinalIgnoreCase))
            {
                LiveProviderComboBox.SelectedItem = item;
                break;
            }
        }

        if (LiveProviderComboBox.SelectedItem == null && LiveProviderComboBox.Items.Count > 0)
        {
            LiveProviderComboBox.SelectedIndex = 0;
        }

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
            var geminiApiKey = ApiKeyTextBox.Text.Trim();
            var openAiApiKey = OpenAiApiKeyTextBox.Text.Trim();
            var openAiTranscriptionModel = OpenAiTranscriptionModelTextBox.Text.Trim();
            var openAiQnaModel = OpenAiQnaModelTextBox.Text.Trim();
            var saveAudio = SaveAudioCheckBox.IsChecked ?? true;
            var captureMicrophone = CaptureMicrophoneCheckBox.IsChecked ?? false;
            var processWithLiveApi = ProcessWithGeminiCheckBox.IsChecked ?? false;
            var showTranscript = ShowTranscriptCheckBox.IsChecked ?? false;
            var saveLocation = SaveLocationTextBox.Text.Trim();
            var selectedProvider = (LiveProviderComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? ProviderGemini;

            if (!saveAudio && !processWithLiveApi)
            {
                MessageBox.Show(Properties.Resources.ValidationRecordingMode,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (processWithLiveApi && string.Equals(selectedProvider, ProviderOpenAi, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(openAiApiKey))
            {
                MessageBox.Show(Properties.Resources.ValidationOpenAiApiKeyRequired,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (processWithLiveApi && string.Equals(selectedProvider, ProviderGemini, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(geminiApiKey))
            {
                MessageBox.Show(Properties.Resources.ValidationGeminiApiKeyRequired,
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(openAiTranscriptionModel))
            {
                openAiTranscriptionModel = DefaultOpenAiTranscriptionModel;
            }

            if (string.IsNullOrWhiteSpace(openAiQnaModel))
            {
                openAiQnaModel = DefaultOpenAiQnaModel;
            }

            if (string.IsNullOrWhiteSpace(saveLocation))
            {
                saveLocation = "";
            }

            var localSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFile);

            var settings = new
            {
                Gemini = new
                {
                    ApiKey = string.IsNullOrWhiteSpace(geminiApiKey) ? "YOUR_API_KEY_HERE" : geminiApiKey
                },
                OpenAI = new
                {
                    ApiKey = string.IsNullOrWhiteSpace(openAiApiKey) ? "YOUR_API_KEY_HERE" : openAiApiKey,
                    Model = openAiTranscriptionModel,
                    TranscriptionModel = openAiTranscriptionModel,
                    QnaModel = openAiQnaModel
                },
                Recording = new
                {
                    SaveAudio = saveAudio,
                    ProcessWithGemini = processWithLiveApi,
                    ShowTranscript = showTranscript,
                    AudioSaveLocation = saveLocation,
                    CaptureMicrophone = captureMicrophone,
                    LiveProvider = selectedProvider
                }
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(localSettingsPath, json);

            var binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LocalSettingsFile);
            if (Path.GetFullPath(localSettingsPath) != Path.GetFullPath(binPath))
            {
                File.WriteAllText(binPath, json);
            }

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
}
