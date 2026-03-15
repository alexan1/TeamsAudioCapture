using System;
using System.IO;

namespace TeamsAudioCapture;

internal static class LocalSettingsPath
{
    private const string LocalSettingsFileName = "appsettings.Local.json";
    private const string AppFolderName = "TeamsAudioCapture";

    public static string GetPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appSettingsDirectory = Path.Combine(localAppData, AppFolderName);
        return Path.Combine(appSettingsDirectory, LocalSettingsFileName);
    }
}
