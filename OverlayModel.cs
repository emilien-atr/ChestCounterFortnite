namespace ChestCounter;

/// <summary>Size and opacity choices of the overlay window (percent), offered in the settings.</summary>
public static class OverlayAppearance
{
    public static readonly int[] Sizes = { 70, 80, 90, 100, 115, 130, 150, 175 };

    /// <summary>Below 30 % the numbers can no longer be read.</summary>
    public static readonly int[] Opacities = { 100, 90, 80, 70, 60, 50, 40, 30 };

    public static int ClampSize(int percent) => Math.Clamp(percent, Sizes[0], Sizes[^1]);
    public static int ClampOpacity(int percent) => Math.Clamp(percent, Opacities[^1], 100);

    /// <summary>Next value of the list, back to the first one after the last (or for a value edited by hand).</summary>
    public static int Next(int[] values, int current) => values[(Array.IndexOf(values, current) + 1) % values.Length];
}

/// <summary>Builds what the overlays (window and OBS page) show from the saved matches and the match in progress.</summary>
public static class OverlayModel
{
    /// <summary>Caption of the right counter between two matches (also used to keep the overlay width stable).</summary>
    public static string LastMatchCaption => L.T("dernière partie", "last match", "última partida");

    /// <param name="live">Figures of the match in progress, when the replay could be read (null otherwise).</param>
    /// <param name="inProgress">A match is being recorded (even if its figures are not readable yet).</param>
    public static OverlayView Build(TrackerData d, MatchRecord? live, bool inProgress, int headStart = 0)
    {
        var liveChests = live?.ChestsOpened ?? 0;
        var liveTreasures = live?.TreasureFinds.Count ?? 0;

        var total = d.Matches.Sum(m => m.ChestsOpened) + liveChests + Math.Max(0, headStart);
        var session = d.Matches.Where(ReportWriter.IsThisSession).ToList();
        var last = d.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault();

        // In game, only the chests: the total and the match being played. Destroyed chests count too (they are already
        // in the total); a single small line says how many. Nothing else: no session, no start, no treasures.
        var destroyed = d.Matches.Sum(m => m.Chests.Count(c => c.Destroyed)) + (live?.Chests.Count(c => c.Destroyed) ?? 0);
        var rows = new List<OverlayRow>();
        if (destroyed > 0)
        {
            rows.Add(new OverlayRow(L.T("dont cassés", "incl. destroyed", "incl. destruidos"), L.N(destroyed)));
        }

        var treasures = d.Matches.Sum(m => m.TreasureFinds.Count) + liveTreasures;
        var sessionChests = session.Sum(m => m.ChestsOpened) + liveChests;

        // Right counter: the match in progress when readable, "…" while it cannot be read yet, else the last match.
        if (live != null)
        {
            return new OverlayView(L.N(total), L.N(live.ChestsOpened), L.T("partie en cours", "current match", "partida actual"), true, L.T("● EN DIRECT", "● LIVE", "● EN DIRECTO"), rows, L.N(treasures), L.N(sessionChests), last != null ? L.N(last.ChestsOpened) : null);
        }
        if (inProgress)
        {
            return new OverlayView(L.N(total), "…", L.T("en cours", "in progress", "en curso"), true, L.T("● PARTIE EN COURS", "● MATCH IN PROGRESS", "● PARTIDA EN CURSO"), rows, L.N(treasures), L.N(sessionChests), last != null ? L.N(last.ChestsOpened) : null);
        }
        return new OverlayView(L.N(total), last != null ? L.N(last.ChestsOpened) : null, LastMatchCaption, false, null, rows, L.N(treasures), L.N(sessionChests), last != null ? L.N(last.ChestsOpened) : null);
    }
}
