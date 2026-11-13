namespace ChestCounter;

/// <summary>Size and opacity choices of the overlay window (percent), offered in the settings.</summary>
public static class OverlayAppearance
{
    public static readonly int[] Sizes = { 50, 55, 60, 70, 80, 90, 100, 115, 130, 150, 175 };

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
        var liveChests = live?.TotalChests ?? 0;
        var liveTreasures = live?.TreasureFinds.Count ?? 0;

        var total = d.Matches.Sum(m => m.TotalChests) + liveChests + Math.Max(0, headStart);
        var session = d.Matches.Where(ReportWriter.IsThisSession).ToList();
        var last = d.Matches.OrderByDescending(m => m.MatchStartUtc ?? m.AnalyzedAtUtc).FirstOrDefault();

        // Destroyed chests are already part of TotalChests: they are added directly to the total, without a
        // separate "dont cassés" line. In game, only the total and the match being played are shown.
        var rows = Array.Empty<OverlayRow>();

        var treasures = d.Matches.Sum(m => m.TreasureFinds.Count) + liveTreasures;
        var sessionChests = session.Sum(m => m.TotalChests) + liveChests;
        var objects = OverlayObjects.Build(d, live);

        // Right counter: the match in progress when readable, "…" while it cannot be read yet, else the last match.
        if (live != null)
        {
            return new OverlayView(L.N(total), L.N(live.TotalChests), L.T("partie en cours", "current match", "partida actual"), true, L.T("● EN DIRECT", "● LIVE", "● EN DIRECTO"), rows, L.N(treasures), L.N(sessionChests), last != null ? L.N(last.TotalChests) : null, objects);
        }
        if (inProgress)
        {
            return new OverlayView(L.N(total), "…", L.T("en cours", "in progress", "en curso"), true, L.T("● PARTIE EN COURS", "● MATCH IN PROGRESS", "● PARTIDA EN CURSO"), rows, L.N(treasures), L.N(sessionChests), last != null ? L.N(last.TotalChests) : null, objects);
        }
        return new OverlayView(L.N(total), last != null ? L.N(last.TotalChests) : null, LastMatchCaption, false, null, rows, L.N(treasures), L.N(sessionChests), last != null ? L.N(last.TotalChests) : null, objects);
    }
}

/// <summary>
/// The ten weekly Treasure Hunt rewards. The grid is filled only by what the replay analyzer found: a reward is
/// crossed out when one of the player's replays contains its golden-chest prize id.
/// </summary>
public static class OverlayObjects
{
    public const int Count = 10;

    public static IReadOnlyList<OverlayObject> Build(TrackerData data, MatchRecord? live)
    {
        var finds = data.Matches.SelectMany(m => m.TreasureFinds);
        if (live != null) finds = finds.Concat(live.TreasureFinds);
        var ids = finds
            .Select(f => f.PrizeId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<OverlayObject>(Count);
        for (var i = 1; i <= Count; i++)
        {
            var id = $"DelilahPrize:dpid_item{i}";
            var found = ids.Contains(id) || ids.Any(x => x.EndsWith(":dpid_item" + i, StringComparison.OrdinalIgnoreCase));
            var known = TreasureNames.Friendly(id);
            var label = known == L.T("Récompense de coffre doré", "Golden chest reward", "Recompensa de cofre dorado")
                ? L.T($"Récompense {i}", $"Reward {i}", $"Recompensa {i}")
                : known;
            result.Add(new OverlayObject(label, found));
        }
        return result;
    }
}
