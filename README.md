# Teams Audio Capture

Simple WASAPI loopback audio recorder for capturing Teams meetings to WAV files.

## Features

- Captures system audio via WASAPI loopback
- Saves recordings to WAV format
- Simple keyboard controls (S to start, Q to stop)
- Saves files to Desktop with timestamp

## Requirements

- Windows OS
- .NET 10.0
- NAudio package

## Usage

```powershell
dotnet run
```

Press **S** to start recording, **Q** to stop recording.

Files are saved to your Desktop as `teams-audio-YYYY-MM-DD_HH-mm-ss.wav`

## Technology

- **NAudio** - Audio capture library
- **WASAPI Loopback** - Windows audio capture API
