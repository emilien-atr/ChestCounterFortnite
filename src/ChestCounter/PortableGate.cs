using System.Diagnostics;
using System.Net.Http;
using System.Windows.Forms;

namespace ChestCounter;

/// <summary>
/// A build run outside an Inno Setup installation is an old portable copy. Portable builds are no longer
/// supported: they must migrate through the official installer, which creates the shortcuts and the uninstaller.
/// </summary>
internal static class PortableGate
{
    internal const string SetupUrl = "https://github.com/emilien-atr/ChestCounterFortnite/releases/latest/download/ChestCounterFortnite-Setup.exe";

    /// <summary>True for an installed build. A build run with "dotnet ...dll" is a development build.</summary>
    public static bool IsInstalled()
    {
        var exe = AppInfo.ExePath;
        if (exe == null)
        {
            return true;
        }
        var dir = Path.GetDirectoryName(exe);
        return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "unins000.exe"));
    }

    /// <summary>Shows the migration message, downloads the setup when needed, then opens it. Returns an exit code.</summary>
    public static int Enforce()
    {
        var exe = AppInfo.ExePath;
        if (exe == null || IsInstalled())
        {
            return 0;
        }

        MessageBox.Show(
            "Cette version portable de ChestCounterFortnite n'est plus prise en charge.\n\n" +
            "L'installateur officiel va s'ouvrir pour installer la dernière version. " +
            "Tu pourras ensuite lancer ChestCounter depuis le raccourci créé.\n\n" +
            "This portable version is no longer supported. The official installer will open to install the latest version.\n\n" +
            "Esta versión portátil ya no es compatible. El instalador oficial se abrirá para instalar la última versión.",
            AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);

        var localSetup = Path.Combine(Path.GetDirectoryName(exe) ?? ".", "ChestCounterFortnite-Setup.exe");
        var setup = File.Exists(localSetup) ? localSetup : null;
        if (setup == null)
        {
            using var download = new InstallerDownloadForm();
            if (download.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(download.SetupPath))
            {
                return 1;
            }
            setup = download.SetupPath;
        }

        try
        {
            Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true })?.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Impossible d'ouvrir l'installateur.\n" +
                "Télécharge-le depuis : " + AppInfo.ReleasesUrl + "\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

/// <summary>Small progress window used while the portable build downloads the official installer.</summary>
internal sealed class InstallerDownloadForm : Form
{
    private readonly Label _status;
    private readonly System.Windows.Forms.ProgressBar _progress;

    public string? SetupPath { get; private set; }

    public InstallerDownloadForm()
    {
        Text = AppInfo.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ClientSize = new Size(500, 128);

        _status = new Label
        {
            AutoSize = false,
            Location = new Point(20, 22),
            Size = new Size(460, 44),
            Text = "Téléchargement de l'installateur officiel…\nDownloading the official installer…",
        };
        _progress = new System.Windows.Forms.ProgressBar
        {
            Location = new Point(20, 78),
            Size = new Size(460, 20),
            Minimum = 0,
            Maximum = 100,
        };
        Controls.Add(_status);
        Controls.Add(_progress);
        Shown += async (_, _) => await DownloadAsync();
    }

    private async Task DownloadAsync()
    {
        try
        {
            var target = Path.Combine(Path.GetTempPath(), "ChestCounterFortnite-Setup.exe");
            var partial = target + ".download";
            try { File.Delete(partial); } catch { }
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.VersionText}");
            using var response = await http.GetAsync(PortableGate.SetupUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var destination = File.Create(partial))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    if (total > 0)
                    {
                        _progress.Value = (int)Math.Clamp(received * 100 / total, 0, 100);
                    }
                }
            }

            var info = new FileInfo(partial);
            if (info.Length < 1_000_000)
            {
                throw new IOException("Le fichier téléchargé est incomplet.");
            }
            using (var check = File.OpenRead(partial))
            {
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                {
                    throw new IOException("Le fichier téléchargé n'est pas un installateur Windows.");
                }
            }
            if (File.Exists(target))
            {
                File.Delete(target);
            }
            File.Move(partial, target);
            SetupPath = target;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            try { File.Delete(Path.Combine(Path.GetTempPath(), "ChestCounterFortnite-Setup.exe.download")); } catch { }
            MessageBox.Show(
                "Impossible de télécharger l'installateur.\n" +
                "Télécharge-le depuis : " + AppInfo.ReleasesUrl + "\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
