using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ChestCounter;

/// <summary>
/// Normal interface: a real Windows window with the figures, buttons and settings.
/// The design is an HTML page drawn by the WebView2 runtime that ships with Windows;
/// the console screen is still available with --console (development / terminal use).
/// </summary>
internal static class GuiApp
{
    public static int Run(Store store, Config config, TrackerData data, Tracker tracker, bool auto, string dataDir, bool reanalyzing)
    {
        var exit = 0;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.SetColorMode(SystemColorMode.Dark); } catch { /* older runtime */ }
                if (config.LastVersion != AppInfo.VersionText)
                {
                    config.LastVersion = AppInfo.VersionText;
                    store.SaveConfig(config);
                }
                using var window = new AppWindow(store, config, data, tracker, auto, dataDir, reanalyzing);
                Application.Run(window);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Interface", ex);
                MessageBox.Show(ex.ToString(), AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                exit = 1;
            }
        })
        { IsBackground = false, Name = "Interface" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }
}

internal sealed class AppWindow : Form
{
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private readonly Store _store;
    private readonly Config _config;
    private readonly TrackerData _data;
    private readonly Tracker _tracker;
    private readonly bool _auto;
    private readonly string _dataDir;
    private readonly bool _reanalyzing;
    private readonly TextWriter _originalOut;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly NotifyIcon _tray = new();
    private readonly ToolStripMenuItem _trayOpen;
    private readonly ToolStripMenuItem _trayReport;
    private readonly ToolStripMenuItem _trayQuit;
    private readonly WebView2 _web = new();
    private readonly List<string> _activity = new();
    private ReplayHealth _health = new(false, false, null, null);
    private bool _healthWarned;
    private DateTime _nextHealthCheck = DateTime.MinValue;
    private DateTime _nextUpdateCheck = DateTime.UtcNow.AddSeconds(10);
    private DateTime _nextDiscordUpdate = DateTime.MinValue;
    private bool _checkingUpdate;
    private ReplayStorageStats _replayStorage = ReplayStorageStats.Empty;
    private DateTime _nextReplayStorage = DateTime.MinValue;
    private DateTime _nextReplayMaintenance = DateTime.UtcNow.AddSeconds(30);
    private bool _replayBusy;

    private FileSystemWatcher? _watcher;
    private volatile bool _dirty;
    private DateTime _nextScan = DateTime.UtcNow.AddSeconds(2);
    private DateTime _lastLive = DateTime.MinValue;
    private string _overlaySignature = "";
    private string? _broadcastUrl;
    private bool _closing;
    private bool _sawFortnite;
    private DateTime? _fortniteClosedAt;
    private bool _webReady;
    private bool _webInitialized;
    private string? _lastState;

    public AppWindow(Store store, Config config, TrackerData data, Tracker tracker, bool auto, string dataDir, bool reanalyzing)
    {
        _store = store;
        _config = config;
        _data = data;
        _tracker = tracker;
        _auto = auto;
        _dataDir = dataDir;
        _reanalyzing = reanalyzing;

        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Text = $"{AppInfo.Name} {AppInfo.VersionText}";
        BackColor = Color.FromArgb(9, 5, 16);

        // The design is 1524x1026: use as much of the screen as possible, keeping its proportions.
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var scale = Math.Min(1.0, Math.Min((work.Width - 64.0) / 1524.0, (work.Height - 64.0) / 1026.0));
        ClientSize = new Size(Math.Max(760, (int)(1524 * scale)), Math.Max(512, (int)(1026 * scale)));
        MinimumSize = new Size(760, 512);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ShowInTaskbar = !_auto;
        Opacity = _auto ? 0 : 1;
        KeyPreview = true;

        _web.Dock = DockStyle.Fill;
        _web.BackColor = Color.FromArgb(9, 5, 16);
        _web.DefaultBackgroundColor = Color.FromArgb(9, 5, 16);
        _web.AllowExternalDrop = false;
        _web.Visible = false;
        Controls.Add(_web);

        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
            {
                Icon = icon;
                _tray.Icon = icon;
            }
        }
        catch
        {
            _tray.Icon = SystemIcons.Application;
        }

        var menu = new ContextMenuStrip();
        _trayOpen = new ToolStripMenuItem();
        _trayReport = new ToolStripMenuItem();
        _trayQuit = new ToolStripMenuItem();
        _trayOpen.Click += (_, _) => RestoreWindow();
        _trayReport.Click += (_, _) => OpenReport();
        _trayQuit.Click += (_, _) => Close();
        menu.Items.Add(_trayOpen);
        menu.Items.Add(_trayReport);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayQuit);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.Text = AppInfo.Name;

        _timer.Tick += OnTick;
        _originalOut = Console.Out;
        Console.SetOut(new UiLog(AppendActivity));
        RebuildWatcher();
        LocalizeTray();
        RefreshReplayStorage(force: true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var round = 2; // DWMWCP_ROUND: native Windows 11 rounded corners + shadow
            if (DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) != 0)
            {
                return;
            }
            var margins = new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
        }
        catch
        {
            // older Windows: plain rectangular borderless window
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_auto)
        {
            HideToTray(notify: false);
        }
        else
        {
            Opacity = 1;
            Activate();
        }
        LocalizeTray();
        _broadcastUrl = OverlayServer.Start(_store);
        if (_config.ShowOverlay)
        {
            StartOverlay();
        }
        _timer.Start();
        DiscordPresence.Start();
        UpdateDiscordPresence(force: true);
        AppendActivity(L.T("Prêt : les coffres sont comptés quelques secondes après chaque partie.",
            "Ready: chests are counted a few seconds after each match.",
            "Listo: los cofres se cuentan unos segundos después de cada partida."));
        RefreshHealth(force: true);
        if (_reanalyzing)
        {
            AppendActivity(L.T("Nouvelle version de l'analyse : les replays encore présents vont être réanalysés (sans doublon).",
                "New analysis version: the replays still on disk will be analyzed again (no duplicates).",
                "Nueva versión del análisis: las repeticiones del disco se analizarán de nuevo (sin duplicados)."));
        }
        if (!Directory.Exists(_config.DemosFolder))
        {
            AppendActivity(L.T("Dossier des replays introuvable : Fortnite le crée à la première partie enregistrée.",
                "Replays folder not found: Fortnite creates it with the first recorded match.",
                "Carpeta de repeticiones no encontrada: Fortnite la crea con la primera partida grabada."));
        }
        BeginInvoke(new Action(() =>
        {
            try
            {
                ReportWriter.WriteAll(_store, _config, _data);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Rapport", ex);
            }
        }));
        await InitializeWebAsync();
    }

    private async Task InitializeWebAsync()
    {
        if (_webInitialized)
        {
            return;
        }
        _webInitialized = true;
        try
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.DataFolderName, "WebView2", CacheKey());
            Directory.CreateDirectory(cache);
            var environment = await CoreWebView2Environment.CreateAsync(null, cache);
            await _web.EnsureCoreWebView2Async(environment);
            if (IsDisposed)
            {
                return;
            }
            var core = _web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.ProcessFailed += (_, args) => ErrorLog.Write("Interface", new Exception("WebView2: " + args.ProcessFailedKind));
            core.NavigationCompleted += (_, _) =>
            {
                _web.Visible = true;
                _web.Focus();
            };
            core.NavigateToString(LoadHtml());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Interface", ex);
            var message = L.T(
                "L'interface n'a pas pu démarrer : le composant « Microsoft Edge WebView2 Runtime » manque sur ce PC.\n\nInstalle-le depuis le site de Microsoft Edge, puis relance ChestCounterFortnite.",
                "The interface could not start: the \"Microsoft Edge WebView2 Runtime\" is missing on this PC.\n\nInstall it from the Microsoft Edge website, then start ChestCounterFortnite again.",
                "La interfaz no pudo iniciarse: falta el componente «Microsoft Edge WebView2 Runtime» en este PC.\n\nInstálalo desde la web de Microsoft Edge y vuelve a abrir ChestCounterFortnite.");
            MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private static string LoadHtml()
    {
        using var stream = typeof(AppWindow).Assembly.GetManifestResourceStream("app.html")
            ?? throw new InvalidOperationException("app.html is missing from the program resources.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string CacheKey()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_dataDir).TrimEnd('\\', '/').ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 4);
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        try
        {
            switch (type)
            {
                case "ready":
                    _webReady = true;
                    PushState(force: true);
                    Post(new { type = "tab", tab = "home" });
                    break;
                case "drag":
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                    break;
                case "minimize":
                    MinimizeRequested();
                    break;
                case "close":
                case "quit":
                    Close();
                    break;
                case "report":
                    OpenReport();
                    break;
                case "overlay":
                    ToggleOverlay();
                    break;
                case "copyBroadcast":
                    CopyBroadcastLink();
                    break;
                case "openBroadcast":
                    OpenBroadcastLink();
                    break;
                case "lock":
                    if (OperatingSystem.IsWindows() && NativeOverlay.IsRunning)
                    {
                        NativeOverlay.ToggleLock();
                        PushState(force: true);
                    }
                    break;
                case "news":
                    DismissNews();
                    break;
                case "browse":
                    BrowseFolder();
                    break;
                case "demos":
                    OpenDemosFolder();
                    break;
                case "replayArchive":
                    _ = ArchiveReplaysAsync();
                    break;
                case "replayCleanup":
                    if (root.TryGetProperty("days", out var daysElement) && daysElement.TryGetInt32(out var days))
                    {
                        _ = CleanupReplaysAsync(days);
                    }
                    break;
                case "replayOpenArchive":
                    OpenReplayArchive();
                    break;
                case "save":
                    if (root.TryGetProperty("settings", out var settings))
                    {
                        SaveSettings(settings);
                    }
                    break;
                case "error":
                    ErrorLog.Write("Interface", new Exception(root.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown script error"));
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Interface", ex);
            Toast(L.T("Une erreur est survenue dans la fenêtre.", "Something went wrong in the window.", "Ha ocurrido un error en la ventana."), "error");
        }
    }

    private void Post(object message)
    {
        if (!_webReady || _web.CoreWebView2 == null)
        {
            return;
        }
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
        catch
        {
            // the window is closing
        }
    }

    private void Toast(string message, string kind) => Post(new { type = "toast", message, kind });

    private void PushState(bool force)
    {
        if (!_webReady || _web.CoreWebView2 == null || _closing)
        {
            return;
        }
        var json = BuildStateJson();
        if (!force && json == _lastState)
        {
            return;
        }
        _lastState = json;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // the window is closing
        }
    }

    private string BuildStateJson()
    {
        var view = Profiles.View(_data, _config);
        var matches = view.Matches;
        var head = HeadStart.Total(_config);
        var total = matches.Sum(m => m.TotalChests) + head;
        var session = matches.Where(ReportWriter.IsThisSession).Sum(m => m.TotalChests);
        var last = matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault();
        var current = Profiles.Current(_data, _config);
        var profiles = Profiles.List(_data);
        var counted = CountedByKind(_data, _config);
        var newsBullets = ReleaseNotes.WhatsNew(ReleaseNotes.Embedded, L.Code);

        var parts = new List<object>();
        if (_config.HeadStartChests > 0)
        {
            parts.Add(new { text = L.Count(_config.HeadStartChests, "classique", "classic", "clásico"), cls = "gold" });
        }
        if (_config.HeadStartRareChests > 0)
        {
            parts.Add(new { text = L.Count(_config.HeadStartRareChests, "rare", "rare", "raro"), cls = "gold2" });
        }
        if (_config.HeadStartSpriteChests > 0)
        {
            parts.Add(new { text = L.Count(_config.HeadStartSpriteChests, "esprit", "sprite", "espíritu"), cls = "purple" });
        }

        var startLine = parts.Count == 0
            ? L.T("Aucun coffre de départ", "No starting chests", "Sin cofres de inicio")
            : "";
        var lastLine = last != null
            ? $"{L.DateTimeShort((last.MatchStartUtc ?? last.AnalyzedAtUtc).ToLocalTime())}  ·  {Pretty.Playlist(last.Playlist)}  ·  {Pretty.Placement(last)}"
            : "";

        var status = _tracker.IsLive ? "live" : _tracker.IsFinishing || _tracker.HasPending ? "counting" : "waiting";

        var state = new
        {
            type = "state",
            version = AppInfo.VersionText,
            lang = L.Code,
            stats = new
            {
                total = L.N(total),
                session = L.N(session),
                last = last != null ? L.N(last.TotalChests) : "—",
                head = head > 0 ? L.N(head) : "—",
            },
            status,
            hasProfile = current != null,
            profile = new
            {
                name = current?.Name ?? "",
                key = current?.Key,
                matches = current != null ? L.N(current.Matches) : "0",
                chests = current != null ? L.N(current.Chests) : "0",
                list = profiles.Select(p => new { key = p.Key, name = p.Name, chests = L.N(p.Chests), matches = L.N(p.Matches) }).ToList(),
            },
            startParts = parts,
            startLine,
            hasLast = last != null,
            lastLine,
            overlay = new
            {
                show = _config.ShowOverlay,
                running = OperatingSystem.IsWindows() && NativeOverlay.IsRunning,
                visible = OperatingSystem.IsWindows() && NativeOverlay.IsVisible,
                locked = OperatingSystem.IsWindows() && NativeOverlay.IsLocked,
            },
            broadcast = new
            {
                url = _broadcastUrl ?? "",
                running = !string.IsNullOrEmpty(_broadcastUrl),
            },
            replay = new
            {
                warning = _health.HasWarning,
                title = _health.Title,
                message = _health.Message,
                last = _health.LastLine,
                action = _health.ActionLabel,
            },
            news = _config.NewsDismissed != AppInfo.VersionText && newsBullets.Count > 0,
            newsBullets,
            replayStorage = new
            {
                folderCount = L.N(_replayStorage.FolderCount),
                folderCountValue = _replayStorage.FolderCount,
                folderSize = L.Size(_replayStorage.FolderBytes),
                archiveCount = L.N(_replayStorage.ArchiveCount),
                archiveCountValue = _replayStorage.ArchiveCount,
                archiveSize = L.Size(_replayStorage.ArchiveBytes),
                archiveEnabled = _config.ReplayArchiveEnabled,
                retention = _config.ReplayArchiveRetentionDays,
            },
            activity = _activity.Count > 0 ? _activity[^1] : "",
            settings = new
            {
                lang = L.Code,
                showOverlay = _config.ShowOverlay,
                style = _config.OverlayStyle,
                size = _config.OverlaySize,
                opacity = _config.OverlayOpacity,
                tray = _config.MinimizeToTray,
                withFortnite = _config.StartWithFortnite,
                discordPresence = _config.DiscordPresence,
                replayArchive = _config.ReplayArchiveEnabled,
                replayRetention = _config.ReplayArchiveRetentionDays,
                classic = counted.Classic + Math.Max(0, _config.HeadStartChests),
                rare = counted.Rare + Math.Max(0, _config.HeadStartRareChests),
                sprite = counted.Sprite + Math.Max(0, _config.HeadStartSpriteChests),
                demos = _config.DemosFolder,
                profile = current?.Key,
            },
            sizes = OverlayAppearance.Sizes,
            opacities = OverlayAppearance.Opacities,
        };
        return JsonSerializer.Serialize(state);
    }

    private void MinimizeRequested()
    {
        if (_config.MinimizeToTray && !_auto)
        {
            HideToTray(notify: true);
        }
        else
        {
            WindowState = FormWindowState.Minimized;
        }
    }

    private void DismissNews()
    {
        _config.NewsDismissed = AppInfo.VersionText;
        _store.SaveConfig(_config);
        PushState(force: true);
    }

    private void BrowseFolder()
    {
        using var pick = new FolderBrowserDialog
        {
            SelectedPath = _config.DemosFolder,
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true,
            Description = L.T("Dossier des replays Fortnite", "Fortnite replays folder", "Carpeta de repeticiones de Fortnite"),
        };
        if (pick.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(pick.SelectedPath))
        {
            Post(new { type = "folder", path = pick.SelectedPath });
        }
        _web.Focus();
    }

    private void SaveSettings(JsonElement settings)
    {
        var warning = "";
        if (L.Normalize(GetString(settings, "lang")) is { } lang)
        {
            _config.Language = lang;
            L.Set(lang);
            LocalizeTray();
            Text = $"{AppInfo.Name} {AppInfo.VersionText}";
        }

        _config.ShowOverlay = GetBool(settings, "showOverlay", _config.ShowOverlay);
        var style = GetString(settings, "style");
        if (style is "hud" or "classic")
        {
            _config.OverlayStyle = style;
        }
        if (GetInt(settings, "size", out var size))
        {
            _config.OverlaySize = Nearest(OverlayAppearance.Sizes, size);
        }
        if (GetInt(settings, "opacity", out var opacity))
        {
            _config.OverlayOpacity = Nearest(OverlayAppearance.Opacities, opacity);
        }
        _config.MinimizeToTray = GetBool(settings, "tray", _config.MinimizeToTray);
        _config.DiscordPresence = GetBool(settings, "discordPresence", _config.DiscordPresence);
        var previousRetention = _config.ReplayArchiveRetentionDays;
        _config.ReplayArchiveEnabled = GetBool(settings, "replayArchive", _config.ReplayArchiveEnabled);
        if (GetInt(settings, "replayRetention", out var retention))
        {
            _config.ReplayArchiveRetentionDays = ReplayMaintenance.NormalizeRetention(retention);
        }
        if (previousRetention == 0 && _config.ReplayArchiveRetentionDays > 0)
        {
            var days = _config.ReplayArchiveRetentionDays;
            var confirm = L.T(
                $"Activer la suppression automatique des replays archivés après {days} jours ?\n\nElle est définitive et ne touche que l'archive. Tu pourras la désactiver à tout moment.",
                $"Enable automatic deletion of archived replays after {days} days?\n\nIt is permanent and only affects the archive. You can turn it off at any time.",
                $"¿Activar la eliminación automática de las repeticiones archivadas después de {days} días?\n\nEs definitiva y solo afecta al archivo. Puedes desactivarla en cualquier momento.");
            if (MessageBox.Show(this, confirm, AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                _config.ReplayArchiveRetentionDays = 0;
            }
        }

        var demos = GetString(settings, "demos");
        if (!string.IsNullOrWhiteSpace(demos))
        {
            _config.DemosFolder = demos.Trim();
        }

        var counted = CountedByKind(_data, _config);
        if (GetInt(settings, "classic", out var classic))
        {
            _config.HeadStartChests = Math.Clamp(classic, 0, 1_000_000) - counted.Classic;
            _config.HeadStartChests = Math.Max(0, _config.HeadStartChests);
        }
        if (GetInt(settings, "rare", out var rare))
        {
            _config.HeadStartRareChests = Math.Max(0, Math.Clamp(rare, 0, 1_000_000) - counted.Rare);
        }
        if (GetInt(settings, "sprite", out var sprite))
        {
            _config.HeadStartSpriteChests = Math.Max(0, Math.Clamp(sprite, 0, 1_000_000) - counted.Sprite);
        }

        var withFortnite = GetBool(settings, "withFortnite", _config.StartWithFortnite);
        if (withFortnite != _config.StartWithFortnite)
        {
            if (OperatingSystem.IsWindows() && FortniteWatch.Apply(withFortnite, _dataDir))
            {
                _config.StartWithFortnite = withFortnite;
            }
            else
            {
                warning = L.T("Ouvrir avec Fortnite : impossible ici, lance le compteur depuis ChestCounterFortnite.exe.",
                    "Open with Fortnite: not possible here, run the counter from ChestCounterFortnite.exe.",
                    "Abrir con Fortnite: no es posible aquí, abre el contador desde ChestCounterFortnite.exe.");
            }
        }

        var profile = GetString(settings, "profile");
        if (!string.IsNullOrEmpty(profile) && Profiles.List(_data).Any(p => p.Key == profile))
        {
            _config.Profile = profile;
        }

        _store.SaveConfig(_config);
        RebuildWatcher();
        _nextHealthCheck = DateTime.MinValue;
        RefreshHealth(force: true);
        try
        {
            ReportWriter.WriteAll(_store, _config, _data);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Rapport", ex);
        }
        ApplyOverlayAfterSettings();
        _nextDiscordUpdate = DateTime.MinValue;
        UpdateDiscordPresence(force: true);
        _nextScan = DateTime.UtcNow.AddSeconds(2);
        _nextReplayMaintenance = DateTime.UtcNow.AddSeconds(2);
        _nextReplayStorage = DateTime.MinValue;
        PushState(force: true);
        Post(new { type = "saved" });
        Toast(L.T("Réglages enregistrés.", "Settings saved.", "Ajustes guardados."), "ok");
        if (warning.Length > 0)
        {
            Toast(warning, "error");
        }
    }

    private void ApplyOverlayAfterSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (!_config.ShowOverlay && NativeOverlay.IsRunning)
        {
            NativeOverlay.Stop();
        }
        else if (_config.ShowOverlay)
        {
            if (!NativeOverlay.IsRunning)
            {
                StartOverlay();
            }
            else
            {
                NativeOverlay.SetAppearance(_config.OverlaySize, _config.OverlayOpacity);
                NativeOverlay.SetStyle(_config.OverlayStyle);
                UpdateOverlay(force: true);
            }
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static bool GetInt(JsonElement element, string name, out int result)
    {
        result = 0;
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static int Nearest(int[] values, int value) => values.OrderBy(v => Math.Abs(v - value)).First();

    private void LocalizeTray()
    {
        _trayOpen.Text = L.T("Ouvrir le compteur", "Open the counter", "Abrir el contador");
        _trayReport.Text = L.T("Ouvrir le rapport", "Open the report", "Abrir el informe");
        _trayQuit.Text = L.T("Quitter", "Quit", "Salir");
        _tray.Text = AppInfo.Name;
    }

    private void RefreshState()
    {
        try
        {
            var total = Profiles.View(_data, _config).Matches.Sum(m => m.TotalChests) + HeadStart.Total(_config);
            _tray.Text = $"{AppInfo.Name} — {L.N(total)} {L.T("coffres", "chests", "cofres")}";
            PushState(force: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Interface", ex);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }
        try
        {
            if (_dirty)
            {
                _dirty = false;
                var soon = DateTime.UtcNow.AddSeconds(2);
                if (soon < _nextScan) _nextScan = soon;
            }
            if (DateTime.UtcNow >= _nextScan)
            {
                var added = _tracker.Scan();
                if (_tracker.NewProfilesFound)
                {
                    _tracker.NewProfilesFound = false;
                    AppendActivity(L.T("Plusieurs comptes Fortnite ont enregistré des replays sur ce PC : chacun a son profil.",
                        "Several Fortnite accounts recorded replays on this PC: each one has its own profile.",
                        "Varias cuentas de Fortnite grabaron repeticiones en este PC: cada una tiene su perfil."));
                }
                _tracker.UpdateLive();
                _lastLive = DateTime.UtcNow;
                if (added > 0)
                {
                    ReportWriter.WriteAll(_store, _config, _data);
                    if (_config.ReplayArchiveEnabled)
                    {
                        _nextReplayMaintenance = DateTime.UtcNow.AddSeconds(2);
                    }
                }
                _nextScan = DateTime.UtcNow.AddSeconds(_tracker.IsFinishing ? 1 : _tracker.HasPending ? 15 : 10);
            }
            else if ((DateTime.UtcNow - _lastLive).TotalSeconds >= 2)
            {
                _tracker.UpdateLive();
                _lastLive = DateTime.UtcNow;
            }
            // The replay was just finalized: read it on the next tick instead of waiting for the 10 s scan.
            if (_tracker.IsFinishing)
            {
                var soon = DateTime.UtcNow.AddSeconds(1);
                if (soon < _nextScan)
                {
                    _nextScan = soon;
                }
            }
            RefreshHealth();
            RefreshReplayStorage();
            MaintainReplays();
            RefreshState();
            UpdateOverlay(force: false);
            UpdateDiscordPresence();
            AutoCloseCheck();
            _ = CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            AppendActivity(L.T($"Erreur pendant la surveillance : {ex.Message}",
                $"Error while watching: {ex.Message}",
                $"Error durante la vigilancia: {ex.Message}"));
        }
    }

    /// <summary>
    /// Installed builds follow GitHub: check at startup and then regularly, download the new exe and restart.
    /// A match being recorded or analyzed is never interrupted by an update.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (_checkingUpdate || _closing || AppInfo.ExePath == null || DateTime.UtcNow < _nextUpdateCheck)
        {
            return;
        }
        _checkingUpdate = true;
        _nextUpdateCheck = DateTime.UtcNow.AddMinutes(5);
        try
        {
            var (found, _) = await Updater.CheckDetailedAsync();
            if (found == null || _closing)
            {
                return;
            }
            if (_tracker.IsLive || _tracker.HasPending || _tracker.IsFinishing)
            {
                _nextUpdateCheck = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            var error = await Task.Run(() => Updater.Install(found));
            if (error != null)
            {
                ErrorLog.Write($"Mise à jour {found.Version.ToString(3)}", new Exception(error));
                _nextUpdateCheck = DateTime.UtcNow.AddMinutes(10);
                return;
            }
            AppendActivity(L.T($"Mise à jour {found.Version.ToString(3)} installée : redémarrage…",
                $"Version {found.Version.ToString(3)} installed: restarting…",
                $"Versión {found.Version.ToString(3)} instalada: reiniciando…"));
            var exe = AppInfo.ExePath;
            if (exe == null)
            {
                return;
            }
            Process.Start(new ProcessStartInfo(exe, "--after-update")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            })?.Dispose();
            _closing = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Mise à jour", ex);
            _nextUpdateCheck = DateTime.UtcNow.AddMinutes(10);
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    /// <summary>Refreshes the "Fortnite is not recording" warning without hammering the game log.</summary>
    private void RefreshHealth(bool force = false)
    {
        if (!force && DateTime.UtcNow < _nextHealthCheck)
        {
            return;
        }
        _health = ReplayHealth.Inspect(_config, force);
        _nextHealthCheck = DateTime.UtcNow.AddSeconds(30);
        if (_health.HasWarning && !_healthWarned)
        {
            AppendActivity(_health.Activity);
            _healthWarned = true;
        }
        else if (!_health.HasWarning)
        {
            _healthWarned = false;
        }
    }

    private void OpenDemosFolder()
    {
        try
        {
            if (!Directory.Exists(_config.DemosFolder))
            {
                Toast(L.T("Le dossier des replays n'existe pas encore.", "The replays folder does not exist yet.", "La carpeta de repeticiones aún no existe."), "error");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_config.DemosFolder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Toast(ex.Message, "error");
        }
    }

    private void OpenReport()
    {
        try
        {
            if (!File.Exists(_store.ReportPath))
            {
                ReportWriter.WriteAll(_store, _config, _data);
            }
            Process.Start(new ProcessStartInfo(_store.ReportPath) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            if (_webReady)
            {
                Toast(ex.Message, "error");
            }
            else
            {
                MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void ToggleOverlay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (!_config.ShowOverlay)
        {
            Post(new { type = "tab", tab = "settings" });
            return;
        }
        if (!NativeOverlay.IsRunning)
        {
            StartOverlay();
        }
        else
        {
            NativeOverlay.Toggle();
        }
        RefreshState();
    }

    private void StartOverlay()
    {
        if (!OperatingSystem.IsWindows() || NativeOverlay.IsRunning)
        {
            return;
        }
        NativeOverlay.SetAppearance(_config.OverlaySize, _config.OverlayOpacity);
        if (Array.IndexOf(NativeOverlay.Styles, _config.OverlayStyle) < 0)
        {
            _config.OverlayStyle = "hud";
        }
        NativeOverlay.SetStyle(_config.OverlayStyle);
        NativeOverlay.Start(_config.OverlayX, _config.OverlayY, _config.OverlayLocked,
            (x, y) =>
            {
                _config.OverlayX = x;
                _config.OverlayY = y;
                try { _store.SaveConfig(_config); } catch { /* ignore */ }
            },
            locked =>
            {
                _config.OverlayLocked = locked;
                try { _store.SaveConfig(_config); } catch { /* ignore */ }
            });
        UpdateOverlay(force: true);
        RefreshState();
    }

    /// <summary>Shares the counter as a Discord activity when the Discord desktop client is running.</summary>
    private void UpdateDiscordPresence(bool force = false)
    {
        if (!force && DateTime.UtcNow < _nextDiscordUpdate)
        {
            return;
        }
        _nextDiscordUpdate = DateTime.UtcNow.AddSeconds(10);

        var matches = Profiles.View(_data, _config).Matches;
        var total = matches.Sum(m => m.TotalChests) + HeadStart.Total(_config);
        var session = matches.Where(ReportWriter.IsThisSession).Sum(m => m.TotalChests);
        var inGame = FortniteWatch.IsFortniteRunning();
        var details = inGame
            ? "Fortnite"
            : L.T("Compteur de coffres", "Chest counter", "Contador de cofres");
        var state = total == 0
            ? L.T("Aucun coffre compté pour l'instant", "No chest counted yet", "Ningún cofre contado todavía")
            : L.T($"{L.N(total)} coffres au total · {L.N(session)} cette session",
                  $"{L.N(total)} chests in total · {L.N(session)} this session",
                  $"{L.N(total)} cofres en total · {L.N(session)} esta sesión");
        DiscordPresence.Update(_config.DiscordPresence, details, state);
    }

    private void UpdateOverlay(bool force)
    {
        if (!OperatingSystem.IsWindows() || !_config.ShowOverlay || !NativeOverlay.IsRunning)
        {
            return;
        }
        var view = OverlayModel.Build(Profiles.View(_data, _config), _tracker.LiveMatch, _tracker.IsLive, HeadStart.Total(_config));
        var signature = $"{view.Total}|{view.Current}|{view.CurrentCaption}|{view.Last}|{view.Rows.Count}|{string.Join(";", view.Rows.Select(r => r.Value))}";
        if (!force && signature == _overlaySignature)
        {
            return;
        }
        _overlaySignature = signature;
        NativeOverlay.Update(view);
    }

    private void AutoCloseCheck()
    {
        if (!_auto || !OperatingSystem.IsWindows())
        {
            return;
        }
        if (FortniteWatch.IsFortniteRunning())
        {
            _sawFortnite = true;
            _fortniteClosedAt = null;
            return;
        }
        if (_sawFortnite && _fortniteClosedAt == null)
        {
            _fortniteClosedAt = DateTime.UtcNow;
        }
        if (_fortniteClosedAt is { } closed && DateTime.UtcNow - closed > TimeSpan.FromSeconds(60))
        {
            Close();
        }
    }

    private void RefreshReplayStorage(bool force = false)
    {
        if (!force && DateTime.UtcNow < _nextReplayStorage)
        {
            return;
        }
        _nextReplayStorage = DateTime.UtcNow.AddSeconds(10);
        _replayStorage = ReplayMaintenance.Inspect(_store, _config);
    }

    /// <summary>Automatic archive/retention pass, disabled by default and run periodically.</summary>
    private void MaintainReplays()
    {
        if (_replayBusy || _closing || DateTime.UtcNow < _nextReplayMaintenance)
        {
            return;
        }
        _nextReplayMaintenance = DateTime.UtcNow.AddMinutes(10);
        if (!_config.ReplayArchiveEnabled && _config.ReplayArchiveRetentionDays <= 0
            && !ReplayMaintenance.HasLegacyArchive(_store))
        {
            return;
        }
        _replayBusy = true;
        _ = MaintainReplaysAsync();
    }

    private async Task MaintainReplaysAsync()
    {
        // Capture the replay list on the UI thread: the tracker may add matches while the files are moved.
        var archivePlan = _config.ReplayArchiveEnabled
            ? ReplayMaintenance.PlanArchive(_config, _data)
            : new ReplayArchivePlan(0, 0, Array.Empty<FileInfo>());
        var retention = _config.ReplayArchiveRetentionDays;
        try
        {
            var result = await Task.Run(() =>
                ReplayMaintenance.Maintain(_store, archivePlan.Files, _config.ReplayArchiveEnabled, retention));
            if (!result.Changed)
            {
                return;
            }
            RefreshReplayStorage(force: true);
            if (result.Compressed > 0)
            {
                var compressed = L.Count(result.Compressed, "replay archivé", "replays archivés", "archived replay", "archived replays", "repetición archivada", "repeticiones archivadas");
                AppendActivity(L.T($"{compressed} compressé(s) en ZIP ({L.Size(result.CompressedBytes)}).", $"{compressed} compressed to ZIP ({L.Size(result.CompressedBytes)}).", $"{compressed} comprimida(s) en ZIP ({L.Size(result.CompressedBytes)})."));
            }
            if (result.Archived > 0)
            {
                var moved = L.Count(result.Archived, "replay analysé", "replays analysés", "analyzed replay", "analyzed replays", "repetición analizada", "repeticiones analizadas");
                AppendActivity(L.T($"{moved} archivé(s) en ZIP.", $"{moved} archived as ZIP.", $"{moved} archivada(s) en ZIP."));
            }
            if (result.Deleted > 0)
            {
                var deleted = L.Count(result.Deleted, "replay archivé", "replays archivés", "archived replay", "archived replays", "repetición archivada", "repeticiones archivadas");
                AppendActivity(L.T($"{deleted} supprimé(s) de l'archive ({L.Size(result.DeletedBytes)}).", $"{deleted} deleted from the archive ({L.Size(result.DeletedBytes)}).", $"{deleted} eliminada(s) del archivo ({L.Size(result.DeletedBytes)})."));
            }
            PushState(force: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay maintenance", ex);
        }
        finally
        {
            _replayBusy = false;
            _nextReplayStorage = DateTime.MinValue;
        }
    }

    private async Task ArchiveReplaysAsync()
    {
        if (_replayBusy || _closing)
        {
            return;
        }
        var plan = ReplayMaintenance.PlanArchive(_config, _data);
        if (plan.Count == 0)
        {
            Toast(L.T("Aucun replay déjà analysé à archiver pour l'instant.", "No already analyzed replay to archive yet.", "Todavía no hay ninguna repetición analizada para archivar."), "ok");
            return;
        }

        var count = L.Count(plan.Count, "replay analysé", "replays analysés", "analyzed replay", "analyzed replays", "repetición analizada", "repeticiones analizadas");
        var message = L.T(
            $"Déplacer {count} ({L.Size(plan.Bytes)}) vers l'archive ZIP unique ?\n\nIls quitteront le dossier Fortnite et seront ajoutés à replays.zip. Tu pourras les supprimer plus tard depuis les réglages.",
            $"Move {count} ({L.Size(plan.Bytes)}) to the single ZIP archive?\n\nThey will leave the Fortnite folder and be added to replays.zip. You can delete them later from the settings.",
            $"¿Mover {count} ({L.Size(plan.Bytes)}) al único archivo ZIP?\n\nSaldrán de la carpeta de Fortnite y se añadirán a replays.zip. Podrás eliminarlas más tarde desde los ajustes.");
        if (MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _replayBusy = true;
        try
        {
            var result = await Task.Run(() => ReplayMaintenance.ArchiveFiles(_store, plan.Files));
            RefreshReplayStorage(force: true);
            PushState(force: true);
            if (result.Archived > 0)
            {
                var moved = L.Count(result.Archived, "replay", "replays", "replay", "replays", "repetición", "repeticiones");
                Toast(L.T($"{moved} archivé(s) en ZIP ({L.Size(result.ArchivedBytes)}).", $"{moved} archived as ZIP ({L.Size(result.ArchivedBytes)}).", $"{moved} archivada(s) en ZIP ({L.Size(result.ArchivedBytes)})."), result.Failed > 0 ? "error" : "ok");
            }
            else
            {
                Toast(L.T("Aucun replay n'a pu être archivé.", "No replay could be archived.", "No se pudo archivar ninguna repetición."), "error");
            }
            if (result.Failed > 0)
            {
                ErrorLog.Write("Replay archive", new Exception($"{result.Failed} replay(s) could not be archived"));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay archive", ex);
            Toast(L.T("Impossible d'archiver les replays.", "Could not archive the replays.", "No se pudieron archivar las repeticiones."), "error");
        }
        finally
        {
            _replayBusy = false;
            _nextReplayStorage = DateTime.MinValue;
        }
    }

    private async Task CleanupReplaysAsync(int days)
    {
        if (_replayBusy || _closing)
        {
            return;
        }
        days = ReplayMaintenance.NormalizeRetention(days);
        if (days <= 0)
        {
            Toast(L.T("Choisis d'abord une durée de conservation.", "Choose a retention period first.", "Elige primero un período de conservación."), "error");
            return;
        }
        var plan = ReplayMaintenance.PlanDelete(_store, days);
        if (plan.Count == 0)
        {
            Toast(L.T($"Aucun replay archivé n'a plus de {days} jours.", $"No archived replay is older than {days} days.", $"Ninguna repetición archivada tiene más de {days} días."), "ok");
            return;
        }

        var count = L.Count(plan.Count, "replay archivé", "replays archivés", "archived replay", "archived replays", "repetición archivada", "repeticiones archivadas");
        var message = L.T(
            $"Supprimer définitivement {count} ({L.Size(plan.Bytes)}) ?\n\nSeuls les replays archivés depuis plus de {days} jours sont concernés. Les replays du dossier Fortnite ne sont pas touchés.",
            $"Permanently delete {count} ({L.Size(plan.Bytes)})?\n\nOnly replays archived for more than {days} days are affected. Replays in the Fortnite folder are not touched.",
            $"¿Eliminar definitivamente {count} ({L.Size(plan.Bytes)})?\n\nSolo se verán afectadas las repeticiones archivadas hace más de {days} días. Las de la carpeta de Fortnite no se tocan.");
        if (MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        _replayBusy = true;
        try
        {
            var result = await Task.Run(() => ReplayMaintenance.DeleteExpired(_store, days));
            RefreshReplayStorage(force: true);
            PushState(force: true);
            var deleted = L.Count(result.Deleted, "replay", "replays", "replay", "replays", "repetición", "repeticiones");
            Toast(L.T($"{deleted} supprimé(s) ({L.Size(result.DeletedBytes)}).", $"{deleted} deleted ({L.Size(result.DeletedBytes)}).", $"{deleted} eliminada(s) ({L.Size(result.DeletedBytes)})."), result.Failed > 0 ? "error" : "ok");
            if (result.Failed > 0)
            {
                ErrorLog.Write("Replay cleanup", new Exception($"{result.Failed} replay(s) could not be deleted"));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Replay cleanup", ex);
            Toast(L.T("Impossible de supprimer les anciens replays.", "Could not delete the old replays.", "No se pudieron eliminar las repeticiones antiguas."), "error");
        }
        finally
        {
            _replayBusy = false;
            _nextReplayStorage = DateTime.MinValue;
        }
    }

    private void OpenReplayArchive()
    {
        try
        {
            Directory.CreateDirectory(_store.ReplayArchivePath);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_store.ReplayArchivePath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Toast(ex.Message, "error");
        }
    }

    private void CopyBroadcastLink()
    {
        if (string.IsNullOrWhiteSpace(_broadcastUrl))
        {
            Toast(L.T("Lien de diffusion indisponible : redémarre ChestCounterFortnite.", "Broadcast link unavailable: restart ChestCounterFortnite.", "Enlace de emisión no disponible: reinicia ChestCounterFortnite."), "error");
            return;
        }
        try
        {
            Clipboard.SetText(_broadcastUrl);
            Toast(L.T("Lien copié : colle-le dans une source navigateur OBS ou TikTok Live Studio.", "Link copied: paste it into an OBS or TikTok Live Studio browser source.", "Enlace copiado: pégalo en una fuente de navegador de OBS o TikTok Live Studio."), "ok");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Broadcast link copy", ex);
            Toast(L.T("Impossible de copier le lien.", "Could not copy the link.", "No se pudo copiar el enlace."), "error");
        }
    }

    private void OpenBroadcastLink()
    {
        if (string.IsNullOrWhiteSpace(_broadcastUrl))
        {
            Toast(L.T("Lien de diffusion indisponible : redémarre ChestCounterFortnite.", "Broadcast link unavailable: restart ChestCounterFortnite.", "Enlace de emisión no disponible: reinicia ChestCounterFortnite."), "error");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_broadcastUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Broadcast link open", ex);
            Toast(L.T("Impossible d'ouvrir le lien de diffusion.", "Could not open the broadcast link.", "No se pudo abrir el enlace de emisión."), "error");
        }
    }

    private void RebuildWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            if (!Directory.Exists(_config.DemosFolder))
            {
                return;
            }
            var watcher = new FileSystemWatcher(_config.DemosFolder, "*.replay")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, _) => _dirty = true;
            watcher.Changed += (_, _) => _dirty = true;
            watcher.Renamed += (_, _) => _dirty = true;
            _watcher = watcher;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void AppendActivity(string line)
    {
        if (_closing || IsDisposed)
        {
            return;
        }
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendActivity), line); } catch { /* closing */ }
            return;
        }
        var text = line.TrimEnd();
        if (text.Length == 0)
        {
            return;
        }
        _activity.Add(text);
        if (_activity.Count > 60)
        {
            _activity.RemoveRange(0, 20);
        }
    }

    private void HideToTray(bool notify)
    {
        Hide();
        _tray.Visible = true;
        if (notify && !_auto)
        {
            _tray.ShowBalloonTip(4000, AppInfo.Name,
                L.T("Le compteur continue de compter dans la zone de notification. Clique sur le coffre pour le rouvrir.",
                    "The counter keeps counting in the notification area. Click the chest to open it again.",
                    "El contador sigue contando en el área de notificación. Haz clic en el cofre para volver a abrirlo."),
                ToolTipIcon.Info);
        }
    }

    private void RestoreWindow()
    {
        _tray.Visible = false;
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Opacity = 1;
        Activate();
        _web.Focus();
    }

    private void StartDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _config.MinimizeToTray && !_auto)
        {
            HideToTray(notify: true);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _timer.Stop();
        DiscordPresence.Stop();
        _watcher?.Dispose();
        _tray.Visible = false;
        OverlayServer.Stop();
        if (OperatingSystem.IsWindows())
        {
            NativeOverlay.Stop();
        }
        try { _store.SaveConfig(_config); } catch { /* ignore */ }
        Console.SetOut(_originalOut);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    private static (int Classic, int Rare, int Sprite) CountedByKind(TrackerData d, Config c)
    {
        var kinds = Profiles.View(d, c).Matches.SelectMany(m => m.Chests).Select(x => ChestKinds.Canonical(x.Kind)).ToList();
        return (kinds.Count(k => k == ChestKinds.Normal), kinds.Count(k => k == ChestKinds.Rare), kinds.Count(k => k == ChestKinds.Extraction));
    }

    private sealed class UiLog : TextWriter
    {
        private readonly Action<string> _sink;
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();

        public UiLog(Action<string> sink) => _sink = sink;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            string? line = null;
            lock (_gate)
            {
                if (value == '\n')
                {
                    line = _line.ToString();
                    _line.Clear();
                }
                else if (value != '\r')
                {
                    _line.Append(value);
                }
            }
            if (line != null)
            {
                _sink(line);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
            {
                return;
            }
            foreach (var c in value)
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value ?? "");
            Write('\n');
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }
}

/// <summary>
/// Minimal HTTP server for browser sources such as OBS and TikTok Live Studio. It listens only on the loopback
/// interface and serves the overlay.html page that the counter already writes, so no URL ACL, admin rights or
/// external hosting are needed. Kept beside the window code to avoid another source file.
/// </summary>
internal static class OverlayServer
{
    private static readonly object Gate = new();
    private static readonly int[] CandidatePorts = { 8765, 8766, 8767, 8768, 8769, 8770, 8771, 8772, 8773, 8774, 8775 };
    private static TcpListener? _listener;
    private static CancellationTokenSource? _stopper;
    private static string? _url;

    public static string? Url
    {
        get
        {
            lock (Gate)
            {
                return _url;
            }
        }
    }

    /// <summary>Starts the loopback server and returns the URL to paste into a browser source.</summary>
    public static string? Start(Store store)
    {
        lock (Gate)
        {
            if (_url != null)
            {
                return _url;
            }

            foreach (var port in CandidatePorts.Concat(new[] { 0 }))
            {
                var listener = TryListen(port);
                if (listener == null)
                {
                    continue;
                }

                var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                var stopper = new CancellationTokenSource();
                _listener = listener;
                _stopper = stopper;
                _url = $"http://127.0.0.1:{actualPort}/overlay";
                _ = Task.Run(() => AcceptLoopAsync(listener, store.OverlayPath, stopper.Token));
                return _url;
            }

            ErrorLog.Write("Overlay server", new Exception("No loopback port could be opened for the browser source."));
            return null;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            var stopper = _stopper;
            var listener = _listener;
            _stopper = null;
            _listener = null;
            _url = null;

            try { stopper?.Cancel(); } catch { /* best effort */ }
            try { listener?.Stop(); } catch { /* best effort */ }
        }
    }

    private static TcpListener? TryListen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            try { listener.Stop(); } catch { /* best effort */ }
            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Overlay server: cannot listen on port {port}", ex);
            try { listener.Stop(); } catch { /* best effort */ }
            return null;
        }
    }

    private static async Task AcceptLoopAsync(TcpListener listener, string overlayPath, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            _ = HandleAsync(client, overlayPath, token);
        }
    }

    private static async Task HandleAsync(TcpClient client, string overlayPath, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (read == 0)
                {
                    return;
                }

                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var firstLine = request.Split("\r\n", 2, StringSplitOptions.None)[0];
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("GET only"), token);
                    return;
                }

                var path = parts[1].Split('?', 2)[0];
                if (path == "/favicon.ico")
                {
                    await WriteResponseAsync(stream, 204, "No Content", "text/plain", Array.Empty<byte>(), token);
                    return;
                }
                if (path is not ("/" or "/overlay" or "/overlay.html"))
                {
                    await WriteResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("Not found"), token);
                    return;
                }

                byte[] body;
                try
                {
                    body = await File.ReadAllBytesAsync(overlayPath, token);
                }
                catch
                {
                    body = Encoding.UTF8.GetBytes(FallbackPage);
                }
                await WriteResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", body, token);
            }
            catch (OperationCanceledException)
            {
                // The application is closing or the server was stopped.
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Overlay server request", ex);
            }
        }
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int status, string reason, string contentType, byte[] body, CancellationToken token)
    {
        var head = $"HTTP/1.1 {status} {reason}\r\n"
            + $"Content-Type: {contentType}\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Cache-Control: no-store, no-cache, must-revalidate\r\n"
            + "Pragma: no-cache\r\n"
            + "Connection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes.AsMemory(0, headBytes.Length), token);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body.AsMemory(0, body.Length), token);
        }
    }

    private static string FallbackPage => $$"""
        <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8">
        <meta http-equiv="refresh" content="2">
        <title>{{L.T("Overlay coffres", "Chest overlay", "Overlay de cofres")}}</title>
        <style>html,body{margin:0;background:transparent;color:#e2e2e8;font:500 16px/1.4 "Segoe UI",system-ui,sans-serif}
        .box{display:inline-block;margin:8px;padding:14px 18px;border-radius:14px;background:rgba(18,18,22,.86);border:1px solid #3a3a42}
        b{color:#f2b84b}</style></head><body><div class="box"><b>{{L.T("Overlay en préparation…", "Overlay is starting…", "Overlay en preparación…")}}</b>
        <div>{{L.T("Garde ChestCounterFortnite ouvert.", "Keep ChestCounterFortnite open.", "Mantén ChestCounterFortnite abierto.")}}</div></div></body></html>
        """;
}
