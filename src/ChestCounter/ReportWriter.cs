using System.Globalization;
using System.Net;
using System.Text;

namespace ChestCounter;

public static class Pretty
{
    public static string Playlist(string? playlist)
    {
        if (string.IsNullOrEmpty(playlist))
        {
            return L.T("mode inconnu", "unknown mode", "modo desconocido");
        }

        var p = playlist.ToLowerInvariant();
        var parts = new List<string>();
        if (p.Contains("habanero") || p.Contains("ranked")) parts.Add(L.T("Classée", "Ranked", "Clasificatoria"));
        if (p.Contains("nobuild")) parts.Add(L.T("Zéro construction", "Zero Build", "Cero construcción"));
        if (p.Contains("solo")) parts.Add(L.T("Solo", "Solo", "Solitario"));
        else if (p.Contains("duo")) parts.Add(L.T("Duo", "Duos", "Dúos"));
        else if (p.Contains("trio")) parts.Add(L.T("Trio", "Trios", "Tríos"));
        else if (p.Contains("squad")) parts.Add(L.T("Section", "Squads", "Escuadrones"));
        return parts.Count > 0 ? string.Join(" · ", parts) : playlist.Replace("Playlist_", "");
    }

    public static string Placement(MatchRecord m)
    {
        if (m.Placement is not int place || place <= 0)
        {
            return L.T("place inconnue", "unknown placement", "puesto desconocido");
        }
        if (place == 1)
        {
            return L.T("Top 1 !", "Victory Royale!", "¡Victoria magistral!");
        }
        return m.TotalPlayers is int total && total > 0 ? $"{L.Ordinal(place)} / {total}" : L.Ordinal(place);
    }

    public static string Clock(float seconds)
    {
        var s = (int)seconds;
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>"à 7:16 · Coffre rare" (certain) or "vers 5:04 · Coffre rare (probable)".</summary>
    public static string Obtained(TreasureFind f)
    {
        if (f.ObtainedAtSeconds is not float t)
        {
            return L.T("moment et source inconnus", "time and source unknown", "momento y origen desconocidos");
        }
        var when = f.TimeExact ? $"{L.T("à", "at", "a los")} {Clock(t)}" : $"{L.T("vers", "around", "hacia los")} {Clock(t)}";
        return f.SourceCertain ? $"{when} · {f.SourceLabel}" : $"{when} · {f.SourceLabel} ({Likely})";
    }

    public static string Likely => L.T("probable", "likely", "probable");

    public static string ItemName(string item)
    {
        var name = item;
        foreach (var prefix in new[] { "WID_", "Athena", "AmmoData" })
        {
            name = name.Replace(prefix, "");
        }
        return name.Replace("_", " ").Trim();
    }
}

/// <summary>Replays analyzed so far / to analyze, shown as a loading card in the report.</summary>
public sealed record ScanProgress(int Done, int Total);

public static class ReportWriter
{
    public static readonly DateTime SessionStartUtc = DateTime.UtcNow;

    /// <summary>Set while several replays are being analyzed: the report shows a loading card and refreshes faster.</summary>
    public static volatile ScanProgress? Progress;

    /// <summary>A match belongs to this session if it ended after the tool was started (backlog replays do not count).</summary>
    public static bool IsThisSession(MatchRecord m)
    {
        var start = m.MatchStartUtc ?? m.AnalyzedAtUtc;
        if (start.Kind == DateTimeKind.Unspecified)
        {
            start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        }
        var end = start.ToUniversalTime().AddSeconds(m.DurationSeconds ?? 0);
        return end >= SessionStartUtc.AddMinutes(-2);
    }
    private static CultureInfo Fr => L.Culture;

    public static void WriteAll(Store store, Config config, TrackerData data)
    {
        try
        {
            var view = Profiles.View(data, config);
            WriteAtomic(store.ReportPath, Report(config, view, Profiles.List(data)));
            var overlay = OverlayModel.Build(view, null, false, HeadStart.Total(config));
            WriteAtomic(store.GameOverlayPath, GameOverlay(overlay));
            WriteAtomic(store.CsvPath, Csv(view));
        }
        catch (Exception ex)
        {
            Console.WriteLine(L.T($"Impossible d'écrire le rapport : {ex.Message}", $"Cannot write the report: {ex.Message}", $"No se puede escribir el informe: {ex.Message}"));
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static DateTime When(MatchRecord m) => (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

    // "Logbook" look (direction A): parchment cards on a night-sea background (a day version follows Windows' light
    // mode). Titles use the embedded Pirata One font; figures use Bahnschrift, installed with Windows.
    private const string Style = """
        :root{color-scheme:dark light;--bg:#070a10;--panel:#0d131d;--panel2:#121a26;--line:rgba(255,255,255,.08);--ink:#e9eef7;--ink2:#fff;--muted:#8b96ab;--muted2:#68738a;--gold:#f6b93b;--gold2:#ffd166;--violet:#8b5cf6;--green:#34d399;--track:rgba(255,255,255,.07);--shadow:0 26px 60px -32px rgba(0,0,0,.9);--radius:20px}
        @media (prefers-color-scheme:light){:root:not([data-theme="dark"]){color-scheme:light;--bg:#f3f6fb;--panel:#fff;--panel2:#fbfcfe;--line:#e4e9f2;--ink:#1b2436;--ink2:#0b1020;--muted:#5f6b80;--muted2:#8b96ab;--gold:#b7791f;--gold2:#8a5a0a;--track:#eaeef6;--shadow:0 20px 50px -30px rgba(20,30,60,.35)}}
        *{box-sizing:border-box}
        body{margin:0;background:radial-gradient(1100px 600px at 10% -10%,rgba(139,92,246,.18),transparent 60%),radial-gradient(900px 520px at 95% -6%,rgba(246,185,59,.12),transparent 58%),var(--bg);color:var(--ink);font:15px/1.55 "Segoe UI Variable Text","Segoe UI",Inter,system-ui,-apple-system,sans-serif;-webkit-font-smoothing:antialiased;min-height:100vh}
        main{max-width:1180px;margin:0 auto;padding:36px 18px 64px;display:flex;flex-direction:column;gap:22px}
        .num{font-variant-numeric:tabular-nums;letter-spacing:-.02em}
        header{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:16px}
        .brand{display:flex;align-items:center;gap:15px;min-width:0}
        .logo{width:54px;height:54px;flex:none;filter:drop-shadow(0 10px 22px rgba(246,185,59,.22))}
        h1{margin:0;font-size:27px;line-height:1.1;letter-spacing:-.03em;color:var(--ink2)}
        h1 span{color:var(--gold)}
        .who{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-top:6px;font-size:13px;color:var(--muted)}
        .who b{color:var(--ink2);font-weight:600}
        .chip{display:inline-flex;align-items:center;gap:8px;padding:7px 12px;border:1px solid var(--line);border-radius:999px;background:var(--panel);font-size:12px;color:var(--muted);white-space:nowrap}
        .dot{width:7px;height:7px;border-radius:50%;background:var(--green);box-shadow:0 0 10px rgba(52,211,153,.9)}
        h2{margin:0;font-size:17px;letter-spacing:-.01em;color:var(--ink2)}
        h2 .num{font-size:20px}
        .head{display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:10px}
        .badge{display:inline-flex;align-items:center;padding:4px 10px;border:1px solid var(--line);border-radius:999px;background:var(--panel);font-size:12px;color:var(--muted)}
        section{display:flex;flex-direction:column;gap:12px}
        .card{background:linear-gradient(180deg,var(--panel2),var(--panel));border:1px solid var(--line);border-radius:var(--radius);box-shadow:var(--shadow)}
        .hero{position:relative;overflow:hidden;padding:28px;display:grid;grid-template-columns:1.5fr 1fr;gap:24px;align-items:center}
        .hero:before{content:"";position:absolute;right:-120px;top:-160px;width:520px;height:520px;background:radial-gradient(circle,rgba(246,185,59,.16),transparent 62%);pointer-events:none}
        .eyebrow{font-size:11px;letter-spacing:.2em;text-transform:uppercase;color:var(--muted)}
        .hero-value{font-size:clamp(60px,10vw,108px);font-weight:800;line-height:.92;color:var(--gold2);text-shadow:0 12px 44px rgba(246,185,59,.25)}
        .hero-sub{margin-top:12px;font-size:14px;color:var(--muted)}
        .hero-side{display:flex;flex-direction:column;gap:10px}
        .stat{display:flex;align-items:center;justify-content:space-between;gap:14px;padding:13px 16px;border:1px solid var(--line);border-radius:14px;background:rgba(255,255,255,.025)}
        .stat span{font-size:12px;color:var(--muted)}
        .stat b{font-size:21px;color:var(--ink2)}
        .grid{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px}
        .kpi{padding:18px 20px}
        .kpi .k{font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--muted)}
        .kpi .v{font-size:31px;font-weight:800;line-height:1.1;color:var(--ink2);margin-top:7px}
        .kpi .s{font-size:12px;color:var(--muted2);margin-top:5px}
        .two{display:grid;grid-template-columns:1.5fr 1fr;gap:16px;align-items:start}
        .pad{padding:18px 20px}
        .chart{padding:18px 20px 12px}
        .bars{display:flex;align-items:stretch;gap:5px;height:176px;padding-top:30px}
        .col{flex:1;min-width:4px;display:flex;align-items:flex-end;position:relative}
        .bar{width:100%;border-radius:6px 6px 2px 2px;background:linear-gradient(180deg,var(--gold2),var(--gold));transition:filter .15s}
        .bar.top{background:linear-gradient(180deg,#c4b5fd,var(--violet))}
        .bar.zero{background:var(--track);height:3px!important}
        .col:hover .bar{filter:brightness(1.15)}
        .tip{position:absolute;left:50%;transform:translateX(-50%);white-space:nowrap;background:#0b1020;color:#fff;padding:4px 9px;border-radius:8px;font-size:12px;font-weight:600;opacity:0;pointer-events:none;transition:opacity .15s;z-index:2;border:1px solid rgba(255,255,255,.14)}
        .col:nth-child(-n+3) .tip{left:0;transform:none}
        .col:nth-last-child(-n+3) .tip{left:auto;right:0;transform:none}
        .col:hover .tip{opacity:1}
        .axis{display:flex;justify-content:space-between;gap:8px;color:var(--muted2);font-size:12px;margin-top:12px;border-top:1px dashed var(--line);padding-top:9px}
        table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
        th,td{text-align:left;padding:11px 9px;border-bottom:1px solid var(--line);vertical-align:middle}
        th{font-size:10px;letter-spacing:.14em;text-transform:uppercase;color:var(--muted2);font-weight:600;white-space:nowrap}
        tr:last-child td{border-bottom:none}
        tbody tr:hover td{background:rgba(255,255,255,.03)}
        td.n{text-align:right;font-weight:700}
        td.nw{white-space:nowrap}
        .big{font-size:18px;color:var(--ink2)}
        .items{color:var(--muted);font-size:13px}
        .star{color:#c4b5fd;font-weight:600}
        .gold{color:var(--gold)}
        .sw{display:inline-block;width:9px;height:9px;border-radius:3px;margin-right:10px;vertical-align:0}
        .meter{display:block;width:100%;height:8px;background:var(--track);border-radius:99px;overflow:hidden}
        .meter i{display:block;height:8px;border-radius:99px}
        .rows{display:grid;gap:14px}
        .row{display:grid;grid-template-columns:minmax(120px,1.1fr) 1fr 54px;gap:12px;align-items:center;font-size:14px}
        .row .lbl{color:var(--ink);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .row .val{text-align:right;font-weight:700;color:var(--ink2)}
        .row.muted{opacity:.65}
        .tag{font-size:10px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted2);border:1px solid var(--line);border-radius:6px;padding:1px 5px;margin-left:6px}
        .flag{display:flex;gap:12px;align-items:flex-start;border:1px solid rgba(248,113,113,.4);background:rgba(248,113,113,.09);border-radius:12px;padding:12px 14px;font-size:13px;color:var(--ink);margin-top:12px}
        .flag b{color:#fca5a5}
        tr.unusual td{background:rgba(248,113,113,.07)}
        .warn{color:#fca5a5}
        .by{white-space:nowrap}
        .by svg{vertical-align:-3px;margin-right:6px}
        .teamtoggle{display:inline-flex;align-items:center;gap:10px;min-height:40px;font-size:13px;color:var(--muted);cursor:pointer;user-select:none}
        .teamtoggle input{width:17px;height:17px;accent-color:var(--gold);cursor:pointer}
        .team{display:none}
        body.showteam tr.team{display:table-row}
        body.showteam span.team{display:inline}
        tr.team td{opacity:.75}
        footer{display:flex;flex-wrap:wrap;gap:12px 24px;justify-content:space-between;align-items:flex-start;font-size:12px;line-height:1.6;color:var(--muted2);border-top:1px solid var(--line);padding-top:18px}
        footer .brandline{white-space:nowrap;color:var(--muted)}
        footer .brandline b{color:var(--ink2)}
        footer .explain{max-width:760px}
        .loading{display:flex;gap:16px;align-items:center;padding:16px 20px;background:linear-gradient(180deg,var(--panel2),var(--panel));border:1px solid var(--line);border-radius:var(--radius);box-shadow:var(--shadow)}
        .loading .grow{flex:1;min-width:0}
        .spin{width:30px;height:30px;flex:none;border:3px solid var(--track);border-top-color:var(--gold);border-radius:50%;animation:spin .8s linear infinite}
        @keyframes spin{to{transform:rotate(360deg)}}
        .pbar{height:8px;background:var(--track);border-radius:99px;overflow:hidden;margin:8px 0 6px}
        .pbar i{display:block;height:100%;background:linear-gradient(90deg,var(--gold),var(--gold2))}
        .muted{color:var(--muted)}
        .win{color:var(--gold);font-weight:700}
        .profiles{font-size:12px;color:var(--muted2);margin-top:-10px}
        @media(max-width:900px){.hero{grid-template-columns:1fr}.grid{grid-template-columns:repeat(2,minmax(0,1fr))}.two{grid-template-columns:1fr}}
        @media(max-width:560px){main{padding:22px 12px 44px}.grid{grid-template-columns:1fr 1fr}.row{grid-template-columns:100px 1fr 46px}footer{flex-direction:column}}
        .skip{position:absolute;left:-999px;top:8px;z-index:20;background:var(--gold);color:#111;padding:9px 13px;border-radius:9px;font-weight:700}
        .skip:focus{left:12px}
        h1,h2{text-wrap:balance}
        .teamtoggle{transition:color .18s ease}
        .teamtoggle:hover{color:var(--ink2)}
        .teamtoggle input:focus-visible{outline:2px solid var(--gold);outline-offset:2px}
        .card{transition:border-color .2s ease,box-shadow .2s ease}
        .col .bar{transition:filter .15s ease}
        @keyframes ccf-rise{from{opacity:0;transform:translate3d(0,12px,0)}to{opacity:1;transform:none}}
        @keyframes ccf-grow{from{transform:scaleY(.02)}to{transform:scaleY(1)}}
        .hero,.kpi,.chart,.two>section,.loading{animation:ccf-rise .55s cubic-bezier(.2,.7,.2,1) both}
        .kpi:nth-child(2){animation-delay:.05s}
        .kpi:nth-child(3){animation-delay:.1s}
        .kpi:nth-child(4){animation-delay:.15s}
        .bar{transform-origin:bottom;animation:ccf-grow .6s cubic-bezier(.2,.7,.2,1) both}
        .col:nth-child(3n) .bar{animation-delay:.06s}
        .no-anim *{animation:none!important}
        @media (prefers-reduced-motion: reduce){*,*::before,*::after{animation:none!important;transition:none!important}}
        """;

    /// <summary>@font-face of the embedded headings font (empty if the resource is missing: the page falls back to Segoe UI).</summary>
    private static readonly string FontFace = LoadFontFace();

    private static string LoadFontFace()
    {
        try
        {
            using var s = typeof(ReportWriter).Assembly.GetManifestResourceStream("PirataOne.woff2");
            if (s == null)
            {
                return "";
            }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return "@font-face{font-family:'Pirata One';src:url(data:font/woff2;base64," + Convert.ToBase64String(ms.ToArray()) + ") format('woff2');font-display:swap}";
        }
        catch
        {
            return "";
        }
    }

    private const string Logo = """<svg class="logo" viewBox="0 0 64 64" aria-hidden="true"><defs><linearGradient id="lg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffd166"/><stop offset="1" stop-color="#b56a10"/></linearGradient></defs><path d="M7 31V24c0-8 6-14 15-14h20c9 0 15 6 15 14v7Z" fill="url(#lg)" stroke="#3a1e08" stroke-width="2.5" stroke-linejoin="round"/><path d="M12 25c2-6 6-9 11-10" fill="none" stroke="#fff3c4" stroke-width="2" stroke-linecap="round" opacity=".55"/><rect x="7" y="30" width="50" height="26" rx="5" fill="url(#lg)" stroke="#3a1e08" stroke-width="2.5"/><rect x="7" y="29" width="50" height="6" rx="3" fill="#7a4516"/><rect x="18" y="31" width="6" height="25" fill="#5c3310"/><rect x="40" y="31" width="6" height="25" fill="#5c3310"/><rect x="25" y="33" width="14" height="12" rx="2.5" fill="#ffefc4" stroke="#5c3310" stroke-width="1.5"/><circle cx="32" cy="38" r="1.8" fill="#3a1e08"/><path d="M32 40v4" stroke="#3a1e08" stroke-width="1.8" stroke-linecap="round"/></svg>""";
    private const string TeamIcon = """<svg width="16" height="16" viewBox="0 0 24 24" aria-hidden="true"><circle cx="9" cy="8" r="3.2" fill="none" stroke="#a78bfa" stroke-width="2"/><path d="M3 20c0-3.5 2.7-6 6-6s6 2.5 6 6" fill="none" stroke="#a78bfa" stroke-width="2"/><circle cx="17" cy="9" r="2.6" fill="none" stroke="#a78bfa" stroke-width="2"/><path d="M16 14c3 0 5 2.2 5 5" fill="none" stroke="#a78bfa" stroke-width="2"/></svg>""";
    private const string FlagIcon = """<svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" style="flex:none;margin-top:2px"><path d="M5 21V4M5 4h11l-2 4 2 4H5" fill="none" stroke="#ff8a6a" stroke-width="2" stroke-linejoin="round"/></svg>""";

    private static readonly Dictionary<string, string> KindColor = new()
    {
        [ChestKinds.Normal] = "#f6b93b",
        [ChestKinds.Rare] = "#38bdf8",
        [ChestKinds.Buried] = "#b98a5a",
        [ChestKinds.Extraction] = "#a78bfa",
        [ChestKinds.PosterChest] = "#f472b6",
        [ChestKinds.Other] = "#94a3b8",
    };

    /// <summary>The browser report shown by "Ouvrir le rapport" (new dashboard design).</summary>
    public static string Report(Config config, TrackerData data, IReadOnlyList<Profile> profiles) =>
        ReportSite.Build(config, data, profiles, Progress);

    private static string BuildLegacy(Config config, TrackerData data, IReadOnlyList<Profile> profiles)
    {
        var progress = Progress;
        var matches = data.Matches.OrderByDescending(When).ToList();
        var countedChests = matches.Sum(m => m.TotalChests);
        // Chests opened before the tool started following the replays (set in the settings): added to the total, but
        // kept out of the per-match figures (average, best match, one treasure every N chests).
        var headStart = HeadStart.Total(config);
        var totalChests = countedChests + headStart;
        var avg = matches.Count > 0 ? (double)countedChests / matches.Count : 0;
        var best = matches.Count > 0 ? matches.Max(m => m.TotalChests) : 0;
        var treasures = matches.Sum(m => m.TreasureFinds.Count);
        var teamTreasures = matches.Sum(m => m.TeamTreasureFinds.Count);
        var session = matches.Where(IsThisSession).Sum(m => m.TotalChests);
        var player = matches.Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";
        // Own treasures only: the team's are found with chests that are not counted here.
        var every = treasures > 0 ? (int)Math.Round((double)countedChests / treasures) : 0;

        // By chest type (with the other interactions, not in the total).
        var byKindCounts = matches.SelectMany(m => m.Chests)
            .GroupBy(c => ChestKinds.Canonical(c.Kind))
            .ToDictionary(g => g.Key, g => g.Count());
        // Chests opened before tracking are part of the total: they appear under their own kind here too.
        foreach (var kind in new[] { ChestKinds.Normal, ChestKinds.Rare, ChestKinds.Extraction })
        {
            var before = HeadStart.For(config, kind);
            if (before > 0)
            {
                byKindCounts[kind] = byKindCounts.GetValueOrDefault(kind) + before;
            }
        }
        var byKind = byKindCounts.OrderByDescending(kv => kv.Value).ToList();
        var kindMax = Math.Max(1, byKind.Count > 0 ? byKind.Max(kv => kv.Value) : 1);
        var otherKinds = matches
            .SelectMany(m => m.OtherInteractions.Count > 0
                ? m.OtherInteractions
                : new Dictionary<string, int> { [ChestKinds.AmmoBox] = m.AmmoBoxesOpened, [ChestKinds.OtherContainer] = m.OtherContainersOpened })
            .GroupBy(kv => kv.Key)
            .Select(g => (Kind: g.Key, Count: g.Sum(kv => kv.Value)))
            .Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count)
            .ToList();
        var treasureRows = matches.SelectMany(m => m.TreasureFinds.Select(f => (m, f, Team: false))).ToList();
        var teamRows = matches.SelectMany(m => m.TeamTreasureFinds.Select(f => (m, f, Team: true))).ToList();
        var unusual = treasureRows.Where(r => !r.f.SourceIsChest).ToList();

        var sb = new StringBuilder();
        var updated = L.T(
            $"Mis à jour le {DateTime.Now.ToString("dd/MM/yyyy à HH:mm:ss", Fr)} · actualisation auto",
            $"Updated {DateTime.Now.ToString("d MMM yyyy 'at' HH:mm:ss", Fr)} · auto refresh",
            $"Actualizado el {DateTime.Now.ToString("dd/MM/yyyy 'a las' HH:mm:ss", Fr)} · actualización automática");
        string Kpi(string value, string label, string? sub = null) =>
            $"""<div class="card kpi"><div class="k">{label}</div><div class="v num">{value}</div>{(sub != null ? $"<div class=\"s\">{sub}</div>" : "")}</div>""";
        string MeterRow(string label, int value, string color)
        {
            var w = Math.Max(2, (int)Math.Round(100.0 * value / kindMax));
            return $"""<div class="row"><span class="lbl"><i class="sw" style="background:{color}"></i>{H(label)}</span><span class="meter"><i style="width:{w}%;background:{color}"></i></span><span class="val num">{value.ToString("N0", Fr)}</span></div>""";
        }

        sb.Append($"""
            <!doctype html><html lang="{L.Code}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="refresh" content="{(progress != null ? 2 : 15)}">
            <title>{AppInfo.Name}</title><meta name="theme-color" content="#070a10"><meta name="color-scheme" content="dark light"><style>{Style}</style></head><body><a class="skip" href="#main">{L.T("Aller au contenu", "Skip to content", "Ir al contenido")}</a><main id="main">
            <header>
              <div class="brand">{Logo}<div><h1>ChestCounter<span>Fortnite</span></h1>
                <div class="who">{(player.Length > 0 ? $"<b>{H(player)}</b>" : "")}<span class="chip">v{AppInfo.VersionText}</span></div></div>
              </div>
              <span class="chip"><i class="dot"></i>{updated}</span>
            </header>
            {LoadingCard(progress)}{ProfilesLine(profiles, player)}
            <section class="card hero">
              <div>
                <div class="eyebrow">{L.T("Coffres au total", "Chests in total", "Cofres en total")}</div>
                <div class="hero-value num" aria-live="polite">{totalChests.ToString("N0", Fr)}</div>
                <div class="hero-sub">{L.T("tous les coffres comptés, cassés inclus, d'après tes replays", "all counted chests, broken ones included, from your replays", "todos los cofres contados, incluidos los rotos, según tus repeticiones")}</div>
              </div>
              <div class="hero-side">
                <div class="stat"><span>{L.T("Cette session", "This session", "Esta sesión")}</span><b class="num">{session.ToString("N0", Fr)}</b></div>
                <div class="stat"><span>{L.T("Avant le suivi", "Before tracking", "Antes del seguimiento")}</span><b class="num">{headStart.ToString("N0", Fr)}</b></div>
              </div>
            </section>
            <div class="grid">
              {Kpi(matches.Count.ToString("N0", Fr), L.T("Parties analysées", "Matches analyzed", "Partidas analizadas"))}
              {Kpi(avg.ToString("0.0", Fr), L.T("Coffres par partie", "Chests per match", "Cofres por partida"))}
              {Kpi(best.ToString("N0", Fr), L.T("Record en une partie", "Best match", "Récord en una partida"))}
              {Kpi(treasures.ToString("N0", Fr), L.T("Trésors trouvés", "Treasures found", "Tesoros encontrados"),
                  (every > 0 ? L.T($"1 tous les {every.ToString("N0", Fr)} coffres", $"1 every {every.ToString("N0", Fr)} chests", $"1 cada {every.ToString("N0", Fr)} cofres") : null)
                  + (teamTreasures > 0 ? (every > 0 ? " · " : "") + L.T($"+{teamTreasures} équipe (estimation)", $"+{teamTreasures} team (estimate)", $"+{teamTreasures} equipo (estimación)") : ""))}
            </div>
            """);

        // Last matches, one bar per match (the best one stands out): the chart always shows something, even after a
        // long break between two seasons.
        var chartMatches = matches.Take(20).OrderBy(When).ToList();
        var max = Math.Max(1, chartMatches.Count > 0 ? chartMatches.Max(m => m.TotalChests) : 1);
        var topMatch = chartMatches.MaxBy(m => m.TotalChests);
        string ChartDate(DateTime d) => d.ToString(L.T("dd/MM/yyyy", "d MMM yyyy", "dd/MM/yyyy"), Fr);
        var topText = topMatch != null
            ? $"{L.T("meilleure partie", "best match", "mejor partida")} : {ChartDate(When(topMatch))} · {L.Count(topMatch.TotalChests, "coffre", "chest", "cofre")}"
            : "";
        sb.Append($"""
            <section>
              <div class="head"><h2>{L.T("Coffres par partie", "Chests per match", "Cofres por partida")}</h2><span class="badge">{L.T("20 dernières parties", "latest 20 matches", "últimas 20 partidas")}</span></div>
              <div class="card chart">
            """);
        if (chartMatches.Count == 0)
        {
            sb.Append($"""<div class="items" style="padding:30px 0">{L.T("Aucune partie pour l'instant : les barres apparaîtront après ta première partie.", "No match yet: the bars will appear after your first match.", "Todavía ninguna partida: las barras aparecerán después de tu primera partida.")}</div>""");
        }
        else
        {
            sb.Append($"""<div class="bars">""");
            foreach (var m in chartMatches)
            {
                var v = m.TotalChests;
                var h = (int)Math.Round(100.0 * v / max);
                var tip = $"{When(m).ToString(L.T("ddd dd/MM", "ddd d MMM", "ddd dd/MM"), Fr)}{L.T(" : ", ": ", ": ")}{L.Count(v, "coffre", "chest", "cofre")}";
                var cls = v == 0 ? " zero" : v == max ? " top" : "";
                sb.Append($"<div class=\"col\" aria-label=\"{H(tip)}\"><span class=\"tip\" style=\"bottom:calc({Math.Max(h, 3)}% + 6px)\">{H(tip)}</span><div class=\"bar{cls}\" style=\"height:{h}%\"></div></div>");
            }
            sb.Append("</div>");
        }
        sb.Append($"""
              <div class="axis"><span>{(chartMatches.Count > 0 ? ChartDate(When(chartMatches[0])) : "—")}</span><span>{topText}</span><span>{(chartMatches.Count > 0 ? ChartDate(When(chartMatches[^1])) : "—")}</span></div>
              </div>
            </section>
            """);

        // By chest type | where the player's treasures come from.
        sb.Append($"""
            <div class="two">
              <section>
                <div class="head"><h2>{L.T("Par type de coffre", "By chest type", "Por tipo de cofre")}</h2><span class="badge">{L.T("total", "total", "total")}</span></div>
                <div class="card pad">
                  <div class="rows">
            """);
        foreach (var g in byKind)
        {
            sb.Append(MeterRow(L.Name(g.Key), g.Value, KindColor.GetValueOrDefault(g.Key, "#9a8f7a")));
        }
        if (byKind.Count == 0)
        {
            sb.Append($"""<div class="items">{L.T("Aucun coffre pour l'instant.", "No chest yet.", "Todavía ningún cofre.")}</div>""");
        }
        foreach (var (kind, count) in otherKinds)
        {
            sb.Append($"""<div class="row muted"><span class="lbl">{H(L.Name(kind))} <span class="tag">{L.T("hors total", "not in total", "fuera del total")}</span></span><span></span><span class="val num">{count.ToString("N0", Fr)}</span></div>""");
        }
        sb.Append($"""
                  </div>
                </div>
              </section>
              <section>
                <div class="head"><h2>{L.T("D'où viennent tes trésors", "Where your treasures come from", "De dónde vienen tus tesoros")}</h2></div>
                <div class="card pad">
                  <table>
            """);
        if (treasureRows.Count == 0)
        {
            sb.Append($"""<tr><td class="items">{L.T("Pas encore de trésor.", "No treasure yet.", "Todavía ningún tesoro.")}</td></tr>""");
        }
        foreach (var g in treasureRows.GroupBy(r => r.f.SourceLabel).OrderByDescending(g => g.Count()))
        {
            var isChest = g.First().f.SourceIsChest;
            sb.Append(isChest
                ? $"""<tr><td>{H(g.Key)}</td><td class="n num big">{g.Count()}</td></tr>"""
                : $"""<tr class="unusual"><td class="warn">{H(g.Key)} <span class="items">· {L.T("fiabilité faible", "low reliability", "fiabilidad baja")}</span></td><td class="n num big warn">{g.Count()}</td></tr>""");
        }
        sb.Append("</table>");
        if (unusual.Count > 0)
        {
            var count = L.Count(unusual.Count, "trésor obtenu", "trésors obtenus", "treasure", "treasures", "tesoro obtenido", "tesoros obtenidos");
            sb.Append($"""<div class="flag"><span>""" + L.T(
                $"<b>{count} autrement qu'avec un coffre.</b> Fiabilité faible : il s'agit très probablement d'une erreur dans la façon dont les replays sont analysés.",
                $"<b>{count} obtained other than from a chest.</b> Low reliability: most likely a mistake in the way the replays are analyzed.",
                $"<b>{count} de otra forma que con un cofre.</b> Fiabilidad baja: muy probablemente un error en la forma de analizar las repeticiones.") + "</span></div>");
        }
        sb.Append("</div></section></div>");

        // Treasures: the player's own, then (hidden unless asked) those found by teammates, shared with the whole team.
        if (treasureRows.Count > 0 || teamRows.Count > 0)
        {
            sb.Append($"""
                <section>
                  <div class="head"><h2>{L.T("Trésors trouvés", "Treasures found", "Tesoros encontrados")} <span class="num gold">{treasures.ToString("N0", Fr)}</span></h2>
                    {(teamRows.Count > 0 ? $"<label class=\"teamtoggle\"><input type=\"checkbox\" id=\"showteam\"> {L.T($"Afficher aussi l'équipe (+{teamRows.Count}, estimation)", $"Also show the team (+{teamRows.Count}, estimate)", $"Mostrar también el equipo (+{teamRows.Count}, estimación)")}</label>" : "")}
                  </div>
                  <div class="card"><table>
                    <tr><th>{L.T("Partie", "Match", "Partida")}</th><th>{L.T("Trésor", "Treasure", "Tesoro")}</th><th>{L.T("Obtenu à", "Obtained at", "Momento")}</th><th>{L.T("Source", "Source", "Origen")}</th><th>{L.T("Par", "By", "Por")}</th></tr>
                """);
            foreach (var (m, f, team) in treasureRows.Concat(teamRows).OrderByDescending(r => When(r.m)).ThenBy(r => r.f.ObtainedAtSeconds ?? 0))
            {
                var when = f.ObtainedAtSeconds is float t ? (f.TimeExact ? Pretty.Clock(t) : $"{L.T("vers", "around", "hacia los")} {Pretty.Clock(t)}") : "?";
                string source;
                if (team && string.IsNullOrEmpty(f.Teammate))
                {
                    source = $"<span class=\"items\">{L.T("cofre hors de ta vue", "chest out of your sight", "cofre fuera de tu vista")}</span>";
                }
                else if (!f.SourceIsChest)
                {
                    source = $"<span class=\"warn\">{H(f.SourceLabel)} · {L.T("fiabilité faible", "low reliability", "fiabilidad baja")}</span>";
                }
                else
                {
                    source = H(f.SourceLabel) + (f.SourceCertain ? "" : $" <span class=\"items\">({Pretty.Likely})</span>");
                }
                var who = !team ? L.T("toi", "you", "tú")
                    : TeamIcon + (string.IsNullOrEmpty(f.Teammate) ? L.T("un coéquipier", "a teammate", "un compañero") : H(f.Teammate));
                var cls = team ? "team" : f.SourceIsChest ? "" : "unusual";
                var stars = f.FromGoldenChest ? "<span class=\"gold\">★★</span>" : "<span class=\"star\">★</span>";
                sb.Append($"<tr{(cls.Length > 0 ? $" class=\"{cls}\"" : "")}><td class=\"nw num items\">{L.DateTimeShort(When(m))}</td><td>{stars} <b>{H(f.DisplayName)}</b></td><td class=\"nw num\">{when}</td><td>{source}</td><td class=\"by\">{who}</td></tr>");
            }
            sb.Append("</table></div></section>");
        }

        // Latest matches.
        sb.Append($"""
            <section>
              <div class="head"><h2>{L.T("Dernières parties", "Latest matches", "Últimas partidas")}</h2><span class="badge">{L.T("100 dernières", "latest 100", "últimas 100")}</span></div>
              <div class="card"><table>
                <tr><th>{L.T("Date", "Date", "Fecha")}</th><th>{L.T("Mode", "Mode", "Modo")}</th><th>{L.T("Place", "Placement", "Puesto")}</th><th style="text-align:right">{L.T("Coffres", "Chests", "Cofres")}</th><th>{L.T("Trésor", "Treasure", "Tesoro")}</th></tr>
            """);
        foreach (var m in matches.Take(100))
        {
            var treasure = string.Join("<br>", m.TreasureFinds.Select(f => $"<span class=\"{(f.FromGoldenChest ? "gold" : "star")}\">{(f.FromGoldenChest ? "★★" : "★")} {H(f.DisplayName)}</span>"))
                + string.Concat(m.TeamTreasureFinds.Select((f, i) => $"<span class=\"team\">{(i == 0 && m.TreasureFinds.Count == 0 ? "" : "<br>")}{TeamIcon} ★ {H(f.DisplayName)}</span>"));
            var win = m.Placement == 1;
            sb.Append($"<tr><td class=\"nw num items\">{L.DateTimeShort(When(m))}</td><td>{H(Pretty.Playlist(m.Playlist))}</td><td class=\"nw num{(win ? " win" : "")}\">{H(Pretty.Placement(m))}</td><td class=\"n num big\">{m.TotalChests}</td><td class=\"items\">{treasure}</td></tr>");
        }
        sb.Append("</table></div></section>");

        var script = """<script>(function(){var k='chestcounter-showteam',b=document.getElementById('showteam');var on=false;try{on=localStorage.getItem(k)==='1'}catch(e){}function set(v){document.body.classList.toggle('showteam',v);if(b)b.checked=v}set(on);if(b)b.addEventListener('change',function(){set(b.checked);try{localStorage.setItem(k,b.checked?'1':'0')}catch(e){}})})();</script>""";
        sb.Append(L.T(
            $"""<footer><span class="brandline"><b>{AppInfo.Name}</b> · v{AppInfo.VersionText} · {AppInfo.Author}</span><span class="explain">Compte les coffres que tu as ouverts ou détruits toi-même, d'après tes replays (« rediffusions » dans Fortnite), analysés après chaque partie. Les caisses de munitions et de nourriture ne sont pas comptées. ★★ = coffre doré de la chasse au trésor, détecté de façon certaine. ★ = trésor reçu grâce à un de tes coffres (moment exact), nommé quand l'objet est identifiable. L'icône équipe marque un trésor trouvé par un coéquipier (estimation) ; il n'entre pas dans tes totaux.</span></footer>""",
            $"""<footer><span class="brandline"><b>{AppInfo.Name}</b> · v{AppInfo.VersionText} · {AppInfo.Author}</span><span class="explain">Counts the chests you opened or destroyed yourself, from your replays, analyzed after each match. Ammo boxes and food boxes are not counted. ★★ = golden treasure hunt chest, detected for sure. ★ = treasure received from one of your own chests (exact moment), named when the item can be identified. The team icon marks a treasure found by a teammate (estimate); it is not in your totals.</span></footer>""",
            $"""<footer><span class="brandline"><b>{AppInfo.Name}</b> · v{AppInfo.VersionText} · {AppInfo.Author}</span><span class="explain">Cuenta los cofres que has abierto o destruido tú mismo, a partir de tus repeticiones, analizadas después de cada partida. Las cajas de munición y de comida no se cuentan. ★★ = cofre dorado de la búsqueda del tesoro, detectado con seguridad. ★ = tesoro recibido de uno de tus cofres (momento exacto), con nombre cuando el objeto se puede identificar. El icono de equipo marca un tesoro encontrado por un compañero (estimación); no cuenta en tus totales.</span></footer>"""));
        const string Anim = """<script>(function(){var r=document.documentElement;try{if(sessionStorage.getItem('ccf-seen')||matchMedia('(prefers-reduced-motion: reduce)').matches){r.classList.add('no-anim');return}sessionStorage.setItem('ccf-seen','1')}catch(e){}\nvar el=document.querySelector('.hero-value');if(!el)return;var to=parseInt(el.textContent.replace(/[^0-9]/g,''),10);if(!to||to<2)return;var lang=document.documentElement.lang||'fr';var start=null;function step(t){if(start===null)start=t;var p=Math.min(1,(t-start)/700),e=1-Math.pow(1-p,3);el.textContent=Math.round(to*e).toLocaleString(lang);if(p<1)requestAnimationFrame(step)}requestAnimationFrame(step)})();</script>""";
        sb.Append(script).Append(Anim).Append("</main></body></html>");
        return sb.ToString();
    }

    private static string LoadingCard(ScanProgress? p)
    {
        if (p == null)
        {
            return "";
        }
        var pct = p.Total > 0 ? (int)Math.Round(100.0 * p.Done / p.Total) : 0;
        return L.T(
            $"""<div class="loading"><div class="spin"></div><div class="grow"><b>Analyse de tes replays… {p.Done} / {p.Total}</b><div class="pbar"><i style="width:{pct}%"></i></div><span class="muted">Cette page se met à jour toute seule : les chiffres ci-dessous augmentent au fil de la lecture des replays.</span></div></div>""",
            $"""<div class="loading"><div class="spin"></div><div class="grow"><b>Analyzing your replays… {p.Done} / {p.Total}</b><div class="pbar"><i style="width:{pct}%"></i></div><span class="muted">This page updates by itself: the numbers below grow as the replays are read.</span></div></div>""",
            $"""<div class="loading"><div class="spin"></div><div class="grow"><b>Analizando tus repeticiones… {p.Done} / {p.Total}</b><div class="pbar"><i style="width:{pct}%"></i></div><span class="muted">Esta página se actualiza sola: las cifras de abajo aumentan a medida que se leen las repeticiones.</span></div></div>""");
    }

    private static string ProfilesLine(IReadOnlyList<Profile> profiles, string player)
    {
        var others = profiles.Where(p => p.Name != player).Select(p => H(p.Name)).ToList();
        if (others.Count == 0)
        {
            return "";
        }
        return $"""<div class="profiles muted">{L.T("Autres profils sur ce PC : ", "Other profiles on this PC: ", "Otros perfiles en este PC: ")}{string.Join(", ", others)} · {L.T("« Changer de profil » dans le compteur", "\"Switch profile\" in the counter", "«Cambiar de perfil» en el contador")}</div>""";
    }

    /// <summary>Rewrites only the compact stream overlay page (called on every live update).</summary>

    public static void WriteOverlay(Store store, OverlayView view)
    {
        try
        {
            WriteAtomic(store.GameOverlayPath, GameOverlay(view));
        }
        catch
        {
            // the page is a convenience: never stop the tracker for it
        }
    }

    /// <summary>The compact in-game card, served to OBS/TikTok browser sources as /overlay/game.</summary>
    public static string GameOverlay(OverlayView v)
    {
        var live = v.CurrentLive
            ? $"<span class=\"live\"><i></i>{L.T("LIVE", "LIVE", "LIVE")}</span>"
            : "";
        var currentLabel = v.CurrentLive
            ? L.T("CETTE PARTIE", "THIS MATCH", "ESTA PARTIDA")
            : L.T("DERNIÈRE PARTIE", "LAST MATCH", "ÚLTIMA PARTIDA");
        var totalLabel = L.T("TOTAL", "TOTAL", "TOTAL");
        return $$"""
            <!doctype html><html lang="{{L.Code}}"><head><meta charset="utf-8"><meta http-equiv="refresh" content="2">
            <title>{{L.T("Overlay de jeu", "Game overlay", "Overlay de juego")}}</title>
            <style>
            {{FontFace}}
            *{box-sizing:border-box}
            html,body{margin:0;width:100%;height:100%;background:transparent;overflow:hidden}
            body{display:flex;align-items:flex-start;justify-content:flex-start;padding:clamp(10px,2.2vh,26px) clamp(12px,2vw,32px);color:#f7e9b6;font-family:"Segoe UI",system-ui,sans-serif}
            .card{position:relative;container-type:inline-size;width:min(92vw,960px);min-width:0;padding:clamp(12px,2.4vw,24px) clamp(16px,3.4vw,34px) clamp(14px,2.8vw,26px);overflow:hidden;border:1px solid rgba(243,189,82,.78);border-radius:clamp(14px,1.8vw,22px);
              background:linear-gradient(145deg,#211532 0%,#100d1d 58%,#090b14 100%);
              box-shadow:0 0 0 2px rgba(243,189,82,.14),0 0 28px rgba(139,92,246,.28),0 16px 35px rgba(0,0,0,.55)}
            .card:before{content:"";position:absolute;inset:0;pointer-events:none;background:radial-gradient(45% 90% at 4% 0%,rgba(243,189,82,.12),transparent 70%),radial-gradient(35% 80% at 100% 0%,rgba(138,92,255,.2),transparent 70%)}
            .top{position:relative;display:flex;align-items:center;gap:clamp(7px,1.2vw,12px);min-height:clamp(18px,2.4vw,28px)}
            .brand{display:inline-flex;align-items:center;gap:clamp(5px,1vw,10px);color:#f3bd52;font-size:clamp(9px,1.55cqw,16px);font-weight:800;letter-spacing:.16em}
            .brand svg{width:clamp(18px,3.1cqw,30px);height:clamp(18px,3.1cqw,30px);filter:drop-shadow(0 0 6px rgba(243,189,82,.4))}
            .brand small{color:#9d95af;font-size:clamp(7px,1.05cqw,11px);font-weight:600;letter-spacing:.12em}
            .live{margin-left:auto;display:inline-flex;align-items:center;gap:clamp(4px,.8cqw,8px);padding:clamp(3px,.65cqw,6px) clamp(6px,1.35cqw,12px);border:1px solid rgba(255,95,167,.55);border-radius:999px;background:rgba(255,79,163,.13);color:#ff9ac5;font-size:clamp(7px,1.4cqw,14px);font-weight:800;letter-spacing:.14em}
            .live i{width:clamp(4px,.85cqw,8px);height:clamp(4px,.85cqw,8px);border-radius:50%;background:#ff5fa7;box-shadow:0 0 0 3px rgba(255,95,167,.12)}
            .numbers{position:relative;display:grid;grid-template-columns:1.22fr 1px 1fr;gap:clamp(12px,3.4cqw,34px);align-items:end;margin-top:clamp(8px,1.7cqw,16px)}
            .stat{min-width:0;display:flex;flex-direction:column;gap:3px}
            .label{color:#d6b86f;font-size:clamp(8px,1.45cqw,15px);font-weight:750;letter-spacing:.15em;white-space:nowrap}
            .value{font-family:"Bahnschrift","Arial Black",sans-serif;font-size:clamp(34px,9.2cqw,92px);font-weight:800;font-variant-numeric:tabular-nums;line-height:.92;letter-spacing:-.01em;color:#ffd45e;text-shadow:0 2px 0 #5b2a00,0 0 18px rgba(255,177,36,.3);white-space:nowrap}
            .stat:last-child .value{font-size:clamp(26px,7cqw,70px)}
            .sep{width:1px;height:clamp(46px,9cqw,86px);align-self:center;background:linear-gradient(180deg,transparent,rgba(243,189,82,.65) 18%,rgba(243,189,82,.65) 82%,transparent)}
            @media (max-width:520px){body{padding:12px}.card{width:100%;padding:12px 16px 14px}.numbers{gap:14px}.value{font-size:36px}.stat:last-child .value{font-size:28px} }
            </style></head><body>
            <section class="card">
              <div class="top">
                <span class="brand"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M4 9.5 12 4l8 5.5V20H4V9.5Z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/><path d="M8.5 20v-5h7v5M9 10h6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>{{L.T("COFFRES", "CHESTS", "COFRES")}} <small>CHEST COUNTER</small></span>
                {{live}}
              </div>
              <div class="numbers">
                <div class="stat"><span class="label">{{totalLabel}}</span><b class="value">{{H(v.Total)}}</b></div>
                <div class="sep" aria-hidden="true"></div>
                <div class="stat"><span class="label">{{currentLabel}}</span><b class="value">{{H(v.Current ?? "—")}}</b></div>
              </div>
            </section></body></html>
            """;
    }

    public static string Csv(TrackerData data)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("date;mode;joueur;place;joueurs;coffres;caisses_munitions;tresors;fichier", "date;mode;player;placement;players;chests;ammo_boxes;treasures;file", "fecha;modo;jugador;puesto;jugadores;cofres;cajas_municion;tesoros;archivo"));

        foreach (var m in data.Matches.OrderBy(When))
        {
            var t = string.Join(" + ", m.TreasureFinds.Select(f => f.DisplayName));
            sb.AppendLine(string.Join(";",
                When(m).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Pretty.Playlist(m.Playlist), m.PlayerName, m.Placement, m.TotalPlayers,
                m.TotalChests, m.AmmoBoxesOpened, t, m.ReplayFile));
        }
        return sb.ToString();
    }
}
