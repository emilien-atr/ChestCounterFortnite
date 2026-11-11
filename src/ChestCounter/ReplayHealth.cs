using System.Globalization;
using System.Text;

namespace ChestCounter;

/// <summary>
/// Why the counter has nothing new to read. Fortnite logs
/// "SupportsAutomaticClientReplayRecording returning false due to user settings" when its
/// "Record Replays" setting is off; without a replay nobody can count the chests.
/// </summary>
public sealed record ReplayHealth(
    bool RecordingDisabled,
    bool Stale,
    DateTime? LastReplayUtc,
    DateTime? FortniteLogUtc)
{
    private static readonly object Gate = new();
    private static ReplayHealth _cached = new(false, false, null, null);
    private static DateTime _checkedAtUtc = DateTime.MinValue;
    private static string _cacheKey = "";

    public bool HasWarning => RecordingDisabled || Stale;

    public string LastReplayLabel => LastReplayUtc is { } date
        ? date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
        : L.T("aucun replay trouvé", "no replay found", "no se encontró ninguna repetición");

    public string LastLine => L.T($"Dernier replay : {LastReplayLabel}", $"Last replay: {LastReplayLabel}", $"Última repetición: {LastReplayLabel}");

    public string Title => RecordingDisabled
        ? L.T("Fortnite n'enregistre pas les replays", "Fortnite is not recording replays", "Fortnite no está grabando repeticiones")
        : L.T("Aucun nouveau replay", "No new replay", "Ninguna repetición nueva");

    public string Message => RecordingDisabled
        ? L.T(
            "Fortnite a refusé l'enregistrement lors de la dernière partie : le réglage « Enregistrer les rediffusions de Battle Royale » est désactivé. Dans Fortnite, ouvre Paramètres → Jeu → Rediffusions, active-le, puis clique sur Appliquer. Sans nouveau replay, aucun coffre ne peut être compté.",
            "Fortnite refused to record during the last match: the « Record Battle Royale Replays » setting is off. In Fortnite, open Settings → Game → Replays, turn it on, then click Apply. Without a new replay, no chest can be counted.",
            "Fortnite rechazó la grabación en la última partida: el ajuste « Grabar repeticiones de Battle Royale » está desactivado. En Fortnite, abre Ajustes → Juego → Repeticiones, actívalo y pulsa Aplicar. Sin una repetición nueva, no se puede contar ningún cofre.")
        : L.T(
            $"Fortnite n'a écrit aucun nouveau replay depuis le {LastReplayLabel}. Vérifie que « Enregistrer les rediffusions de Battle Royale » est activé dans Paramètres → Jeu → Rediffusions, puis joue une partie complète et quitte-la normalement.",
            $"Fortnite has not written a new replay since {LastReplayLabel}. Check that « Record Battle Royale Replays » is on in Settings → Game → Replays, then play a full match and leave it normally.",
            $"Fortnite no ha escrito ninguna repetición nueva desde {LastReplayLabel}. Comprueba que « Grabar repeticiones de Battle Royale » está activado en Ajustes → Juego → Repeticiones, juega una partida completa y sal de ella normalmente.");

    public string Activity => RecordingDisabled
        ? L.T("Fortnite n'enregistre pas les replays : active « Enregistrer les rediffusions de Battle Royale » dans ses paramètres.",
            "Fortnite is not recording replays: turn on « Record Battle Royale Replays » in its settings.",
            "Fortnite no graba repeticiones: activa « Grabar repeticiones de Battle Royale » en sus ajustes.")
        : L.T($"Aucun nouveau replay depuis le {LastReplayLabel}. Vérifie le réglage des rediffusions dans Fortnite.",
            $"No new replay since {LastReplayLabel}. Check the replay setting in Fortnite.",
            $"Ninguna repetición nueva desde {LastReplayLabel}. Comprueba el ajuste de repeticiones en Fortnite.");

    public string Status => RecordingDisabled
        ? L.T("refusé par Fortnite", "refused by Fortnite", "rechazado por Fortnite")
        : Stale
            ? L.T("aucun nouveau replay", "no new replay", "ninguna repetición nueva")
            : L.T("OK", "OK", "OK");

    public string ActionLabel => L.T("Ouvrir le dossier des replays", "Open the replays folder", "Abrir la carpeta de repeticiones");

    /// <summary>
    /// Reads the newest Fortnite log and the newest replay. Cached for 20 seconds: the interface asks for it
    /// once per second, but scanning a 30 MB log at that rate would be wasteful.
    /// </summary>
    public static ReplayHealth Inspect(Config config, bool force = false)
    {
        var replay = NewestReplay(config.DemosFolder);
        var log = NewestLog();
        var key = $"{config.DemosFolder}|{replay?.FullName}|{replay?.LastWriteTimeUtc.Ticks}|{log?.FullName}|{log?.LastWriteTimeUtc.Ticks}";

        lock (Gate)
        {
            if (!force && key == _cacheKey && DateTime.UtcNow - _checkedAtUtc < TimeSpan.FromSeconds(20))
            {
                return _cached;
            }

            _cached = Analyze(replay, log);
            _cacheKey = key;
            _checkedAtUtc = DateTime.UtcNow;
            return _cached;
        }
    }

    private static ReplayHealth Analyze(FileInfo? replay, FileInfo? log)
    {
        var lastReplay = replay?.LastWriteTimeUtc;
        if (log == null || DateTime.UtcNow - log.LastWriteTimeUtc > TimeSpan.FromDays(7))
        {
            return new ReplayHealth(false, false, lastReplay, log?.LastWriteTimeUtc);
        }

        var text = ReadTail(log.FullName, 8_000_000);
        var falseIndex = text.LastIndexOf("SupportsAutomaticClientReplayRecording returning false due to user settings", StringComparison.OrdinalIgnoreCase);
        var trueIndex = text.LastIndexOf("SupportsAutomaticClientReplayRecording returning true", StringComparison.OrdinalIgnoreCase);
        var disabled = falseIndex >= 0 && falseIndex > trueIndex;

        if (disabled)
        {
            var refusedAt = TimestampOf(text, falseIndex) ?? log.LastWriteTimeUtc;
            if (lastReplay is { } replayTime && replayTime >= refusedAt.AddSeconds(-2))
            {
                disabled = false; // a replay was written after the refusal: recording worked again
            }
        }

        if (disabled)
        {
            return new ReplayHealth(true, false, lastReplay, log.LastWriteTimeUtc);
        }

        var stale = lastReplay == null || lastReplay < log.LastWriteTimeUtc.AddHours(-24);
        return new ReplayHealth(false, stale, lastReplay, log.LastWriteTimeUtc);
    }

    private static FileInfo? NewestReplay(string folder)
    {
        try
        {
            var directory = new DirectoryInfo(folder);
            return directory.Exists
                ? directory.EnumerateFiles("*.replay", SearchOption.TopDirectoryOnly).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static FileInfo? NewestLog()
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FortniteGame", "Saved", "Logs");
            var directory = new DirectoryInfo(folder);
            return directory.Exists
                ? directory.EnumerateFiles("FortniteGame*.log", SearchOption.TopDirectoryOnly).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Encoding.UTF8.GetString(memory.ToArray());
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Timestamp at the start of the log line containing <paramref name="index"/>, converted to UTC.</summary>
    private static DateTime? TimestampOf(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var open = text.IndexOf('[', lineStart);
        var close = open >= 0 ? text.IndexOf(']', open) : -1;
        if (open < 0 || close <= open)
        {
            return null;
        }

        var stamp = text.Substring(open + 1, close - open - 1);
        if (!DateTime.TryParseExact(stamp, "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return null;
        }

        return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    }
}
