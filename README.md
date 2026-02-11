# Teams Audio Capture

Simple WASAPI loopback audio recorder for capturing Teams meetings to WAV files with optional Gemini Voice API streaming.

## Features

- 🎙️ Captures system audio via WASAPI loopback
- 💾 Saves recordings to WAV format
- 🤖 Optional real-time streaming to Gemini Voice API
- ⌨️ Simple keyboard controls (S to start, Q to stop)
- 📁 Saves files to Desktop with timestamp

## Requirements

- Windows OS
- .NET 10.0
- NAudio package

## Setup

### 1. Clone and build

```powershell
git clone https://github.com/alexan1/TeamsAudioCapture.git
cd TeamsAudioCapture
dotnet restore
dotnet build
```

### 2. Configure Gemini API (Optional)

To enable streaming to Gemini Voice API:

1. Get your API key from [Google AI Studio](https://makersuite.google.com/app/apikey)
2. Open `appsettings.Local.json`
3. Add your API key:

```json
{
  "Gemini": {
    "ApiKey": "your-actual-api-key-here"
  }
}
```

**Note:** `appsettings.Local.json` is git-ignored for security.

## Usage

```powershell
dotnet run
```

**Controls:**
- Press **S** to start recording
- Press **Q** to stop recording

**Output:**
- WAV files saved to Desktop: `teams-audio-YYYY-MM-DD_HH-mm-ss.wav`
- Gemini responses displayed in console (if configured)

## Technology

- **NAudio** - Audio capture library
- **WASAPI Loopback** - Windows audio capture API
- **Gemini Voice API** - Real-time AI audio streaming (optional)

## Project Structure

- `Program.cs` - Main application and audio capture logic
- `GeminiAudioStreamer.cs` - WebSocket client for Gemini API
- `appsettings.json` - Configuration template
- `appsettings.Local.json` - Your local API key (git-ignored)
