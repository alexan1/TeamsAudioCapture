using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Extensions.Configuration;

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
        var apiKey = _configuration["Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey) && apiKey != "YOUR_API_KEY_HERE")
        {
            ApiKeyTextBox.Text = apiKey;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var apiKey = ApiKeyTextBox.Text.Trim();
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                MessageBox.Show("Please enter an API key.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Create or update appsettings.Local.json
            var localSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), LocalSettingsFile);
            
            var settings = new
            {
                Gemini = new
                {
                    ApiKey = apiKey
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
