namespace ChestCounter;

/// <summary>Creates the desktop shortcut once, after a new install or an update.</summary>
internal static class DesktopShortcut
{
    public static void TryCreate(string exePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
            {
                return;
            }

            var shortcutPath = Path.Combine(desktop, AppInfo.Name + ".lnk");
            if (File.Exists(shortcutPath))
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(shortcutPath);
            link.TargetPath = exePath;
            link.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
            link.IconLocation = exePath + ",0";
            link.Description = AppInfo.Name;
            link.Save();
        }
        catch (Exception ex)
        {
            // A locked or redirected Desktop must never prevent the app from starting.
            ErrorLog.Write("Création du raccourci Bureau", ex);
        }
    }
}