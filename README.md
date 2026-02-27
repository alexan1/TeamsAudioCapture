# Teams Audio Capture

Windows desktop application for capturing Teams meetings to WAV files with optional Gemini Voice API streaming.

## Features

- 🎙️ **Captures system audio** via WASAPI loopback (always enabled)
- 🎤 **Optional microphone capture** to include your voice in recordings
- 💾 Saves recordings to WAV format
- 🤖 Optional real-time streaming to Gemini Voice API
- 🖥️ Modern WPF user interface
- 📊 Real-time audio level visualization
- ⏱️ Recording time tracker
- ⚙️ Built-in settings management
- 📁 Saves files with timestamp

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

### 3. Configure Gemini API and Recording Options (Optional)

To customize your recording experience:

1. Click the **Settings** button in the application
2. **Gemini API** (optional): Enter your API key from [Google AI Studio](https://makersuite.google.com/app/apikey)
3. **Recording Mode**:
   - **Save audio files to disk** - Creates WAV files on your computer
   - **Capture microphone** - Include your voice in recordings (system audio always captured)
   - **Process audio with Gemini API** - Real-time AI transcription
   - **Show transcript in main window** - Display AI responses
4. **Audio Save Location**: Choose where to save WAV files (default: Desktop)
5. Click **Save**

The settings are stored locally in `appsettings.Local.json` and are git-ignored for security.

## Usage

### Audio Capture Modes

**System Audio (Always Captured)**
- Captures everything playing through your speakers/headphones
- Includes: Teams meeting participants, shared videos, system sounds
- Uses WASAPI loopback technology

**Microphone (Optional)**
- Enable in Settings → "Capture microphone (your voice)"
- Captures your voice from the default microphone
- Mixed with system audio in real-time
- Ideal for recording complete Teams conversations

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

Click **Settings** to configure:

- **Gemini API Key**: For AI transcription and analysis
- **OpenAI transcription model**: Model used for OpenAI realtime transcription
- **OpenAI Q&A model**: Model used to answer detected questions
- **Save Audio**: Enable/disable saving to WAV files
- **Capture Microphone**: Include your voice (system audio always captured)
- **Process with Gemini**: Enable real-time AI processing
- **Show Transcript**: Display AI responses in main window
- **Save Location**: Custom folder for recordings

All settings are automatically saved and take effect on the next recording session.

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

**Microphone not recording:**
- Check microphone is set as default device in Windows
- Enable "Capture microphone" in Settings
- Check microphone permissions

**Gemini not responding:**
- Verify API key is correct in Settings
- Check internet connection
- Ensure API quota is not exceeded

## License

MIT License - see LICENSE file for details
