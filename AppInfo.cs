namespace ChestCounter;

/// <summary>Name, author, version and the names shared by the tool's processes.</summary>
public static class AppInfo
{
    /// <summary>Name of the application: windows, tray, report, data folder and exe.</summary>
    public const string Name = "ChestCounterFortnite";

    /// <summary>Who made this build. Replay reading is based on LED-N's open-source project (MIT licence).</summary>
    public const string Author = "Emilien";

    /// <summary>Folder under %APPDATA% holding the history, the settings and the report.</summary>
    public const string DataFolderName = Name;

    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => Version.Build > 0 ? $"{Version.Major}.{Version.Minor}.{Version.Build}" : $"{Version.Major}.{Version.Minor}";

    /// <summary>Path of the exe when running as the published app (null under "dotnet ...dll").</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return p != null && Path.GetFileName(p).Equals(Name + ".exe", StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    // Names of the per-user kernel objects that let the processes find each other.
    /// <summary>One instance per data folder (the normal folder, or a test folder given with --data).</summary>
    public static string InstanceMutexFor(string dataDir)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToLowerInvariant()));
        return @"Local\" + Name + ".Instance." + Convert.ToHexString(hash, 0, 8);
    }
    public const string StandbyMutex = @"Local\" + Name + ".Standby";
    public const string StandbyStopEvent = @"Local\" + Name + ".StandbyStop";
}
