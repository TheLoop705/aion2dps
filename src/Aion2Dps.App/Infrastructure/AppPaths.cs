namespace Aion2Dps.App.Infrastructure;

/// <summary>Well-known locations. Data lives in %APPDATA%/Aion2Dps (settings, logs, fight DB); recordings in Documents.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2Dps");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string DefaultCaptureDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aion2Dps", "captures");

    public static string GameDataDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static string Version
    {
        get
        {
            var v = typeof(AppPaths).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Tests redirect the data directory to a temp folder.</summary>
    internal static void OverrideDataDirectory(string path) => DataDirectory = path;
}
