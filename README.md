# Teams Audio Capture

Windows desktop application for capturing Teams meetings to WAV files with optional Gemini Voice API streaming.

## Features

- 🎙️ Captures system audio via WASAPI loopback
- 💾 Saves recordings to WAV format
- 🤖 Optional real-time streaming to Gemini Voice API
- 🖥️ Modern WPF user interface
- 📊 Real-time audio level visualization
- ⏱️ Recording time tracker
- ⚙️ Built-in settings management
- 📁 Saves files to Desktop with timestamp

## Requirements

- Windows OS (Windows 10/11)
- .NET 10.0
- Visual Studio 2022 (for development)

## Setup

### 1. Clone and build

```powershell
git clone https://github.com/alexan1/TeamsAudioCapture.git
cd TeamsAudioCapture
dotnet restore
dotnet build
```

### 2. Run the application

```powershell
dotnet run
```

Or open the solution in Visual Studio and press F5.

### 3. Configure Gemini API (Optional)

To enable streaming to Gemini Voice API:

1. Click the **Settings** button in the application
2. Enter your API key from [Google AI Studio](https://makersuite.google.com/app/apikey)
3. Click **Save**

The API key is stored locally in appsettings.Local.json and is git-ignored for security.

## Usage

### Starting a Recording

1. Launch the application
2. Click **Start Recording** button
3. The application will capture all system audio output
4. Audio level indicator shows recording activity
5. If Gemini API is configured, responses will appear in real-time

### Stopping a Recording

1. Click **Stop Recording** button
2. A dialog will show the saved file location and size
3. WAV files are saved to Desktop: teams-audio-YYYY-MM-DD_HH-mm-ss.wav

### Settings

- Click **Settings** to configure your Gemini API key
- Settings are automatically saved to appsettings.Local.json
- Changes take effect on next recording session

## User Interface

The application features:

- **Status Panel**: Shows recording status, device name, time, and file size
- **Audio Level Bar**: Visual feedback of audio capture
- **Gemini Response Area**: Real-time AI transcription and analysis (when configured)
- **Control Buttons**: Start, Stop, and Settings

## Technology

- **WPF** - Windows Presentation Foundation UI framework
- **NAudio** - Audio capture library
- **WASAPI Loopback** - Windows audio capture API
- **Gemini Voice API** - Real-time AI audio streaming (optional)
- **Microsoft.Extensions.Configuration** - Settings management

## Project Structure

- App.xaml / App.xaml.cs - WPF application entry point
- MainWindow.xaml / MainWindow.xaml.cs - Main UI window
- SettingsWindow.xaml / SettingsWindow.xaml.cs - Settings dialog
- AudioCapturer.cs - Audio capture logic
- GeminiAudioStreamer.cs - WebSocket client for Gemini API
- appsettings.json - Configuration template
- appsettings.Local.json - Your local API key (git-ignored)

## Development

### Building from Source

```powershell
dotnet build
```

### Running Tests

```powershell
dotnet test
```

### Publishing

To create a standalone executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained
```

## Screenshots

[Coming soon]

## Troubleshooting

**No audio being captured:**
- Ensure system audio is playing
- Check Windows audio settings
- Try restarting the application

**Gemini not responding:**
- Verify API key is correct in Settings
- Check internet connection
- Ensure API quota is not exceeded

## License

MIT License - see LICENSE file for details
