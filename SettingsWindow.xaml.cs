using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Extensions.Configuration;
using MessageBox = System.Windows.MessageBox;

namespace TeamsAudioCapture;

public partial class SettingsWindow : Window
{
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
        // Load API Key
        var apiKey = _configuration["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
        {
            ApiKeyTextBox.Text = apiKey;
        }

        // Load recording mode settings
        var saveAudio = _configuration.GetValue<bool>("Recording:SaveAudio", true);
        var captureMicrophone = _configuration.GetValue<bool>("Recording:CaptureMicrophone", false);
        var processWithGemini = _configuration.GetValue<bool>("Recording:ProcessWithGemini", false);
        var showTranscript = _configuration.GetValue<bool>("Recording:ShowTranscript", false);
        var saveLocation = _configuration["Recording:AudioSaveLocation"] ?? "";

        SaveAudioCheckBox.IsChecked = saveAudio;
        CaptureMicrophoneCheckBox.IsChecked = captureMicrophone;
        ProcessWithGeminiCheckBox.IsChecked = processWithGemini;
        ShowTranscriptCheckBox.IsChecked = showTranscript;
        SaveLocationTextBox.Text = string.IsNullOrWhiteSpace(saveLocation) 
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop) 
            : saveLocation;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apiKey = ApiKeyTextBox.Text.Trim();
            var saveAudio = SaveAudioCheckBox.IsChecked ?? true;
            var captureMicrophone = CaptureMicrophoneCheckBox.IsChecked ?? false;
            var processWithGemini = ProcessWithGeminiCheckBox.IsChecked ?? false;
            var showTranscript = ShowTranscriptCheckBox.IsChecked ?? false;
            var saveLocation = SaveLocationTextBox.Text.Trim();

            // Validation
            if (!saveAudio && !processWithGemini)
            {
                MessageBox.Show("Please enable at least one option: Save Audio or Process with Gemini.", 
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (processWithGemini && string.IsNullOrWhiteSpace(apiKey))
            {
                MessageBox.Show("Please enter an API key to process with Gemini.", 
                    "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Use Desktop as default if save location is empty
            if (string.IsNullOrWhiteSpace(saveLocation))
            {
                saveLocation = "";
            }

            // Create or update appsettings.Local.json
            var localSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFile);

            var settings = new
            {
                Gemini = new
                {
                    ApiKey = string.IsNullOrWhiteSpace(apiKey) ? "YOUR_API_KEY_HERE" : apiKey
                },
                Recording = new
                {
                    SaveAudio = saveAudio,
                    ProcessWithGemini = processWithGemini,
                    ShowTranscript = showTranscript,
                    AudioSaveLocation = saveLocation,
                    CaptureMicrophone = captureMicrophone
                }
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            File.WriteAllText(localSettingsPath, json);

            // Also copy to bin directory if different
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
