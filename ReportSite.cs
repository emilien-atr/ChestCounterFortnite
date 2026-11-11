using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ChestCounter;

/// <summary>
/// The browser report: a dark purple dashboard (sidebar, total, evolution chart, chest kinds,
/// last matches) generated as a single self-contained HTML file, with no external assets.
/// </summary>
public static class ReportSite
{
    public static string Build(Config config, TrackerData data, IReadOnlyList<Profile> profiles, ScanProgress? progress)
    {
        var now = DateTimeOffset.Now;
        var matches = data.Matches.OrderByDescending(When).ToList();
        var counted = matches.Sum(m => m.ChestsOpened);
        var head = HeadStart.Total(config);
        var total = counted + head;
        var avg = matches.Count > 0 ? (double)counted / matches.Count : 0;
        var destroyed = matches.Sum(m => m.Chests.Count(c => c.Destroyed));
        var player = matches.Select(m => m.PlayerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                     ?? profiles.FirstOrDefault()?.Name
                     ?? "";
        var profileName = string.IsNullOrWhiteSpace(player) ? L.T("Profil inconnu", "Unknown profile", "Perfil desconocido") : player;

        var firstDay = matches.Count > 0 ? When(matches[^1]).Date : DateTime.Today;
        var lastDay = matches.Count > 0 ? When(matches[0]).Date : DateTime.Today;
        if ((lastDay - firstDay).Days > 2000)
        {
            firstDay = lastDay.AddDays(-2000);
        }

        var daily = matches.GroupBy(m => When(m).Date).ToDictionary(g => g.Key, g => g.Sum(m => m.ChestsOpened));
        var series = new List<(string Date, int Count)>();
        for (var d = firstDay; d <= lastDay; d = d.AddDays(1))
        {
            series.Add((d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), daily.GetValueOrDefault(d)));
        }

        var trendStart = lastDay.AddDays(-6);
        var current7 = matches.Where(m => When(m).Date >= trendStart && When(m).Date <= lastDay).Sum(m => m.ChestsOpened);
        var previous7 = matches.Where(m => When(m).Date >= trendStart.AddDays(-7) && When(m).Date < trendStart).Sum(m => m.ChestsOpened);
        string trend;
        var trendClass = "";
        if (previous7 > 0)
        {
            var percent = (int)Math.Round(100.0 * (current7 - previous7) / previous7);
            trend = L.T(
                $"{percent:+0;-0;0} % par rapport aux 7 jours précédents",
                $"{percent:+0;-0;0}% vs the previous 7 days",
                $"{percent:+0;-0;0} % respecto a los 7 días anteriores");
            if (percent < 0)
            {
                trendClass = " neg";
            }
        }
        else if (current7 > 0)
        {
            trend = L.T($"{N(current7)} coffres sur les 7 derniers jours", $"{N(current7)} chests in the last 7 days", $"{N(current7)} cofres en los últimos 7 días");
        }
        else
        {
            trend = L.T("Aucun coffre sur les 7 derniers jours", "No chest in the last 7 days", "Ningún cofre en los últimos 7 días");
        }

        var kinds = matches.SelectMany(m => m.Chests)
            .GroupBy(c => ChestKinds.Canonical(c.Kind))
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var kind in new[] { ChestKinds.Normal, ChestKinds.Rare, ChestKinds.Extraction })
        {
            kinds[kind] = kinds.GetValueOrDefault(kind) + HeadStart.For(config, kind);
        }
        var byKind = kinds.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();

        var headNormal = HeadStart.For(config, ChestKinds.Normal);
        var headRare = HeadStart.For(config, ChestKinds.Rare);
        var headSprite = HeadStart.For(config, ChestKinds.Extraction);
        var heroNote = matches.Count > 0
            ? head > 0
                ? L.T($"dont {N(head)} avant le suivi · {N(counted)} comptés en {N(matches.Count)} {Parties(matches.Count)}",
                    $"including {N(head)} before tracking · {N(counted)} counted in {N(matches.Count)} {Parties(matches.Count)}",
                    $"incluidos {N(head)} antes del seguimiento · {N(counted)} contados en {N(matches.Count)} {Parties(matches.Count)}")
                : L.T($"{N(counted)} comptés en {N(matches.Count)} {Parties(matches.Count)}",
                    $"{N(counted)} counted in {N(matches.Count)} {Parties(matches.Count)}",
                    $"{N(counted)} contados en {N(matches.Count)} {Parties(matches.Count)}")
            : head > 0
                ? L.T($"dont {N(head)} avant le suivi", $"including {N(head)} before tracking", $"incluidos {N(head)} antes del seguimiento")
                : L.T("Aucune partie pour l'instant", "No match yet", "Ninguna partida todavía");

        var chartNote = head > 0
            ? L.T($"Coffres comptés dans tes parties, hors {N(head)} coffres avant le suivi. Périodes calculées depuis ta dernière partie.",
                $"Chests counted in your matches, excluding the {N(head)} before tracking. Periods end on your last match.",
                $"Cofres contados en tus partidas, sin los {N(head)} anteriores al seguimiento. Los períodos terminan en tu última partida.")
            : L.T("Coffres comptés dans tes parties. Périodes calculées depuis ta dernière partie.",
                "Chests counted in your matches. Periods end on your last match.",
                "Cofres contados en tus partidas. Los períodos terminan en tu última partida.");

        var typeNotes = new List<string>();
        if (head > 0)
        {
            typeNotes.Add(L.T($"Les {N(head)} coffres avant le suivi sont inclus.", $"The {N(head)} chests from before tracking are included.", $"Los {N(head)} cofres anteriores al seguimiento están incluidos."));
        }
        if (destroyed > 0)
        {
            typeNotes.Add(L.T($"dont {N(destroyed)} cassés au lieu d'être ouverts.", $"{N(destroyed)} of them broken instead of opened.", $"{N(destroyed)} de ellos rotos en vez de abiertos."));
        }
        var typeNote = typeNotes.Count > 0 ? $"<div class=\"card-note\">{string.Join("<br>", typeNotes)}</div>" : "";

        var totalMeta = matches.Count > 0
            ? $"{N(matches.Count)} {Parties(matches.Count)} · {D(avg)} {L.T("coffres par partie", "chests per match", "cofres por partida")}"
            : L.T("Aucune partie analysée", "No match analyzed", "Ninguna partida analizada");

        var typeRows = new StringBuilder();
        foreach (var (kind, count) in byKind)
        {
            var percent = total > 0 ? 100.0 * count / total : 0;
            var color = KindColor(kind);
            typeRows.Append($"""
                  <div class="type-row">
                    <span class="type-ico" style="color:{color}"><svg viewBox="0 0 32 32" aria-hidden="true"><use href="#ccf-mini"/></svg></span>
                    <span class="type-name">{H(L.Name(kind))}</span>
                    <span class="type-track"><i style="width:{Math.Max(1.5, percent).ToString("0.#", CultureInfo.InvariantCulture)}%;background:{color}"></i></span>
                    <span class="type-count">{N(count)}</span>
                    <span class="type-pct">{Pct(percent)}</span>
                  </div>
                """);
        }

        var matchRows = new StringBuilder();
        for (var i = 0; i < Math.Min(matches.Count, 100); i++)
        {
            var m = matches[i];
            var extra = i >= 5;
            var detail = Pretty.Playlist(m.Playlist) + " · " + Pretty.Placement(m);
            var broken = m.Chests.Count(c => c.Destroyed);
            if (broken > 0)
            {
                detail += " · " + L.T($"{N(broken)} cassés", $"{N(broken)} broken", $"{N(broken)} rotos");
            }
            matchRows.Append($"""
                  <tr{(extra ? " class=\"extra\" hidden" : "")}>
                    <td>
                      <div class="date-cell"><svg class="ico cal" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-cal"/></svg>
                        <div><span class="date-main">{DateLabel(When(m))}</span><span class="match-sub">{H(detail)}</span></div>
                      </div>
                    </td>
                    <td class="count-cell">{N(m.ChestsOpened)}</td>
                  </tr>
                """);
        }
        if (matches.Count == 0)
        {
            matchRows.Append($"""
                  <tr><td colspan="2" class="empty">{L.T("Aucune partie analysée pour l'instant.", "No match analyzed yet.", "Ninguna partida analizada todavía.")}</td></tr>
                """);
        }

        var seeAll = matches.Count > 5
            ? $"""<button class="btn-more" id="seeAll" type="button" aria-expanded="false"><span>{L.T("Voir tout", "See all", "Ver todo")}</span><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-arrow"/></svg></button>"""
            : "";

        var settingsRows = new StringBuilder();
        void SetRow(string label, string value) => settingsRows.Append($"<div class=\"set-row\"><span>{label}</span><b>{value}</b></div>");
        SetRow(L.T("Langue", "Language", "Idioma"), L.NativeName(L.Code));
        SetRow(L.T("Profil", "Profile", "Perfil"), H(profileName));
        SetRow(L.T("Version", "Version", "Versión"), "v" + AppInfo.VersionText);
        SetRow(L.T("Coffres de départ", "Head-start chests", "Cofres iniciales"),
            $"{N(headNormal)} {L.T("classiques", "classic", "clásicos")} · {N(headRare)} {L.T("rares", "rare", "raros")} · {N(headSprite)} {L.T("esprits", "sprite", "espíritus")}");
        SetRow(L.T("Dossier des replays", "Replays folder", "Carpeta de repeticiones"), H(config.DemosFolder));
        SetRow(L.T("Overlay", "Overlay", "Overlay"),
            $"{L.T("Activé", "On", "Activado")} · {(config.OverlayStyle == "classic" ? L.T("Classique", "Classic", "Clásico") : "HUD")} · {config.OverlaySize} % · {config.OverlayOpacity} %");

        var generatedDisplay = now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var generatedLabel = L.T("Rapport généré le", "Report generated on", "Informe generado el") + " " + generatedDisplay;
        var lastMatchLabel = matches.Count > 0
            ? $"{DateLabel(When(matches[0]))} · {Pretty.Playlist(matches[0].Playlist)} · {Pretty.Placement(matches[0])}"
            : L.T("Aucune partie", "No match", "Ninguna partida");
        var updatedDefault = L.T("Mis à jour il y a quelques secondes", "Updated a few seconds ago", "Actualizado hace unos segundos");
        var footerText = L.T(
            "Coffres ouverts ou détruits par toi, d'après tes replays analysés après chaque partie. Les caisses de munitions et de nourriture ne sont pas comptées. Les coffres avant le suivi sont inclus dans le total ; le graphique part de ta dernière partie.",
            "Chests you opened or destroyed yourself, from your replays analyzed after each match. Ammo and food boxes are not counted. The chests from before tracking are included in the total; the chart ends on your last match.",
            "Cofres abiertos o destruidos por ti, según tus repeticiones analizadas después de cada partida. Las cajas de munición y de comida no se cuentan. Los cofres anteriores al seguimiento están incluidos en el total; el gráfico termina en tu última partida.");
        var profilesNote = profiles.Count > 1
            ? $"<p>{H(L.T("Autres profils :", "Other profiles:", "Otros perfiles:"))} {H(string.Join(", ", profiles.Select(p => p.Name)))}</p>"
            : "";
        var refreshMeta = progress != null ? "<meta http-equiv=\"refresh\" content=\"2\">" : "";
        var loading = progress == null
            ? ""
            : $"""
              <div class="loading card">
                <span class="spinner" aria-hidden="true"></span>
                <div class="load-body">
                  <b>{L.T("Analyse de tes replays…", "Analyzing your replays…", "Analizando tus repeticiones…")} {N(progress.Done)} / {N(progress.Total)}</b>
                  <div class="pbar"><i style="width:{Math.Clamp((int)Math.Round(100.0 * progress.Done / Math.Max(1, progress.Total)), 0, 100)}%"></i></div>
                  <span>{L.T("Le rapport se met à jour tout seul pendant l'analyse.", "The report updates itself while analyzing.", "El informe se actualiza solo durante el análisis.")}</span>
                </div>
              </div>
            """;

        var chartJson = JsonSerializer.Serialize(new
        {
            days = series.Select(x => new { d = x.Date, c = x.Count }).ToList(),
            start = firstDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end = lastDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            generatedAt = now.ToString("o", CultureInfo.InvariantCulture),
            lang = L.Code
        });
        var txtJson = JsonSerializer.Serialize(new
        {
            updatedJustNow = L.T("Mis à jour il y a quelques secondes", "Updated a few seconds ago", "Actualizado hace unos segundos"),
            updatedAgo = L.T("Mis à jour il y a {x}", "Updated {x} ago", "Actualizado hace {x}"),
            minute = L.T("minute", "minute", "minuto"),
            minutes = L.T("minutes", "minutes", "minutos"),
            hour = L.T("heure", "hour", "hora"),
            hours = L.T("heures", "hours", "horas"),
            day = L.T("jour", "day", "día"),
            days = L.T("jours", "days", "días"),
            chests = L.T("coffres", "chests", "cofres"),
            seeAll = L.T("Voir tout", "See all", "Ver todo"),
            seeLess = L.T("Réduire", "Show less", "Ver menos"),
            noChart = L.T("Aucune partie sur cette période.", "No match in this period.", "Ninguna partida en este período."),
            themeToLight = L.T("Passer au thème clair", "Switch to light theme", "Cambiar al tema claro"),
            themeToDark = L.T("Passer au thème sombre", "Switch to dark theme", "Cambiar al tema oscuro")
        });

        var sb = new StringBuilder();
        sb.Append($"""
<!doctype html>
<html lang="{L.Code}" data-theme="dark">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{H(AppInfo.Name)} · {L.T("Rapport", "Report", "Informe")}</title>
<meta name="color-scheme" content="dark light">
<meta name="theme-color" content="#0a0616">
{refreshMeta}
<style>{Style}</style>
{ThemeBoot}
</head>
<body>
{Sprite}
<aside class="side">
  <div class="side-glow" aria-hidden="true"></div>
  <a class="brand" href="#top" aria-label="{H(AppInfo.Name)}">
    <span class="brand-logo"><svg viewBox="0 0 64 64" aria-hidden="true"><use href="#ccf-chest"/></svg></span>
    <span class="brand-name">ChestCounter<em>Fortnite</em></span>
  </a>
  <div class="side-profile"><span class="side-dot" aria-hidden="true"></span>{H(profileName)}</div>
  <nav class="nav" aria-label="{L.T("Navigation", "Navigation", "Navegación")}">
    <a class="nav-item active" href="#top" data-scroll="top"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-home"/></svg>{L.T("Accueil", "Home", "Inicio")}</a>
    <a class="nav-item" href="#stats" data-scroll="stats"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-chart"/></svg>{L.T("Statistiques", "Statistics", "Estadísticas")}</a>
    <button class="nav-item" id="openSettings" type="button"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-settings"/></svg>{L.T("Paramètres", "Settings", "Parámetros")}</button>
  </nav>
  <div class="side-version">v{AppInfo.VersionText}</div>
  {CastleArt}
</aside>
<div class="main">
  <main class="content" id="top">
    <div class="topbar">
      <div class="status-wrap">
        <button class="status-pill" id="statusBtn" type="button" aria-expanded="false" aria-controls="statusPop" title="{H(generatedLabel)}">
          <span class="dot" aria-hidden="true"></span><span id="statusText">{updatedDefault}</span><svg class="ico chev" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-chev"/></svg>
        </button>
        <div class="pop" id="statusPop">
          <div class="pop-head"><b>{L.T("Rapport", "Report", "Informe")}</b><span>v{AppInfo.VersionText}</span></div>
          <div class="pop-row"><span>{L.T("Généré", "Generated", "Generado")}</span><b>{generatedDisplay}</b></div>
          <div class="pop-row"><span>{L.T("Dernière partie", "Last match", "Última partida")}</span><b>{H(lastMatchLabel)}</b></div>
          <button class="pop-refresh" id="refreshBtn" type="button"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-refresh"/></svg>{L.T("Actualiser maintenant", "Refresh now", "Actualizar ahora")}</button>
          <div class="pop-note">{L.T("L'app réécrit ce rapport après chaque partie.", "The app rewrites this report after each match.", "La app reescribe este informe después de cada partida.")}</div>
        </div>
      </div>
      <button class="round" id="themeBtn" type="button" aria-label="{H(L.T("Passer au thème clair", "Switch to light theme", "Cambiar al tema claro"))}"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use id="themeIcon" href="#ic-moon"/></svg></button>
    </div>
    {loading}
    <section class="hero" aria-labelledby="heroLabel">
      {HeroArt}
      <div class="hero-inner">
        <div class="hero-label">
          <span class="hero-badge"><svg viewBox="0 0 64 64" aria-hidden="true"><use href="#ccf-chest"/></svg></span>
          <h1 id="heroLabel">{L.T("Coffres ouverts", "Chests opened", "Cofres abiertos")}</h1>
        </div>
        <div class="hero-value" data-target="{total}">{N(total)}</div>
        <div class="hero-trend{trendClass}" title="{H(L.T($"du {DateLabel(trendStart)} au {DateLabel(lastDay)}", $"from {DateLabel(trendStart)} to {DateLabel(lastDay)}", $"del {DateLabel(trendStart)} al {DateLabel(lastDay)}"))}"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-trend"/></svg><span>{H(trend)}</span></div>
        <div class="hero-note">{H(heroNote)}</div>
      </div>
    </section>
    <section class="card chart-card" id="stats" aria-labelledby="evoTitle">
      <div class="card-head">
        <div class="card-title"><span class="tile"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-chart"/></svg></span><h2 id="evoTitle">{L.T("Évolution", "Evolution", "Evolución")}</h2></div>
        <div class="periods" role="group" aria-label="{L.T("Période", "Period", "Período")}">
          <button class="period active" type="button" data-period="7">{L.T("7j", "7d", "7d")}</button>
          <button class="period" type="button" data-period="30">{L.T("30j", "30d", "30d")}</button>
          <button class="period" type="button" data-period="90">{L.T("90j", "90d", "90d")}</button>
          <button class="period" type="button" data-period="all">{L.T("Tout", "All", "Todo")}</button>
        </div>
      </div>
      <div class="chart" id="chart" aria-live="polite"></div>
      <div class="chart-note">{H(chartNote)}</div>
    </section>
    <div class="two">
      <section class="card type-card" aria-labelledby="typeTitle">
        <div class="card-head">
          <div class="card-title"><span class="tile"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-pie"/></svg></span><h2 id="typeTitle">{L.T("Par type de coffre", "By chest type", "Por tipo de cofre")}</h2></div>
        </div>
        <div class="type-rows">{typeRows}</div>
        {typeNote}
      </section>
      <section class="total-card" aria-labelledby="totalTitle">
        <div class="total-head"><span class="tile"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-info"/></svg></span><h2 id="totalTitle">{L.T("Total par partie", "Total per match", "Total por partida")}</h2></div>
        <div class="total-value">{N(total)}</div>
        <div class="total-sub">{L.T("Coffres ouverts au total", "Chests opened in total", "Cofres abiertos en total")}</div>
        <div class="total-meta">{H(totalMeta)}</div>
        {BalloonArt}
      </section>
    </div>
    <section class="card matches" aria-labelledby="matchesTitle">
      <div class="card-head">
        <div class="card-title"><span class="tile"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-clock"/></svg></span><h2 id="matchesTitle">{L.T("Dernières parties", "Latest matches", "Últimas partidas")}</h2></div>
        {seeAll}
      </div>
      <div class="table-wrap">
        <table>
          <thead><tr><th scope="col">{L.T("Date", "Date", "Fecha")}</th><th scope="col" class="right">{L.T("Coffres ouverts", "Chests opened", "Cofres abiertos")}</th></tr></thead>
          <tbody>{matchRows}</tbody>
        </table>
      </div>
    </section>
    <footer>
      <div class="foot-brand"><b>{H(AppInfo.Name)}</b><span>v{AppInfo.VersionText} · {H(AppInfo.Author)}</span><span>·</span><span>{H(generatedLabel)}</span></div>
      <p>{H(footerText)}</p>
      {profilesNote}
    </footer>
  </main>
</div>
<div class="modal" id="settingsModal" role="dialog" aria-modal="true" aria-labelledby="settingsTitle" aria-hidden="true">
  <div class="modal-card" role="document">
    <div class="modal-head"><h2 id="settingsTitle">{L.T("Paramètres", "Settings", "Parámetros")}</h2><button class="round small" id="closeSettings" type="button" aria-label="{L.T("Fermer", "Close", "Cerrar")}"><svg class="ico" viewBox="0 0 24 24" aria-hidden="true"><use href="#ic-close"/></svg></button></div>
    <p class="modal-sub">{L.T("Ces réglages se modifient dans ChestCounterFortnite → Réglages.", "These settings can be changed in ChestCounterFortnite → Settings.", "Estos ajustes se cambian en ChestCounterFortnite → Parámetros.")}</p>
    <div class="set-rows">{settingsRows}</div>
    <button class="btn-primary" id="closeSettings2" type="button">{L.T("Fermer", "Close", "Cerrar")}</button>
  </div>
</div>
<script>window.CCF_DATA = {chartJson}; window.CCF_TXT = {txtJson};</script>
<script>{Script}</script>
</body>
</html>
""");
        return sb.ToString();
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.GetCultureInfo(L.Code switch { "en" => "en-GB", "es" => "es-ES", _ => "fr-FR" }));

    private static string D(double n) => n.ToString("0.0", CultureInfo.GetCultureInfo(L.Code switch { "en" => "en-GB", "es" => "es-ES", _ => "fr-FR" }));

    private static string Pct(double p) => (p >= 10 ? p.ToString("0", CultureInfo.GetCultureInfo(L.Code switch { "en" => "en-GB", "es" => "es-ES", _ => "fr-FR" })) : p.ToString("0.#", CultureInfo.GetCultureInfo(L.Code switch { "en" => "en-GB", "es" => "es-ES", _ => "fr-FR" }))) + " %";

    private static string Parties(int n) => n == 1
        ? L.T("partie", "match", "partida")
        : L.T("parties", "matches", "partidas");

    private static string DateLabel(DateTime d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static DateTime When(MatchRecord m) => (m.MatchStartUtc ?? m.AnalyzedAtUtc).ToLocalTime();

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string KindColor(string kind) => ChestKinds.Canonical(kind) switch
    {
        ChestKinds.Normal => "#f6b73c",
        ChestKinds.Rare => "#38bdf8",
        ChestKinds.Extraction => "#a78bfa",
        ChestKinds.Buried => "#fb923c",
        ChestKinds.PosterChest => "#f472b6",
        _ => "#94a3b8",
    };

    private const string ThemeBoot = """
        <script>try{var t=localStorage.getItem("ccf-theme");if(t==="light")document.documentElement.dataset.theme="light"}catch(e){}</script>
        """;

    private const string Sprite = """
        <svg class="sprite" aria-hidden="true" focusable="false">
          <defs>
            <linearGradient id="ccfGold" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffe9a8"/><stop offset=".45" stop-color="#f6b73c"/><stop offset="1" stop-color="#b45309"/></linearGradient>
            <linearGradient id="ccfBlue" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#93c5fd"/><stop offset="1" stop-color="#2563eb"/></linearGradient>
            <symbol id="ccf-chest" viewBox="0 0 64 64">
              <ellipse cx="32" cy="56" rx="24" ry="4" fill="#000" opacity=".35"/>
              <path d="M8 31c0-10 7-16 17-16h14c10 0 17 6 17 16v4H8z" fill="url(#ccfGold)" stroke="#6b2e05" stroke-width="2"/>
              <rect x="8" y="33" width="48" height="21" rx="4" fill="#8a4b12" stroke="#6b2e05" stroke-width="2"/>
              <rect x="8" y="31" width="48" height="7" fill="url(#ccfBlue)"/>
              <rect x="28" y="29" width="8" height="27" fill="url(#ccfBlue)"/>
              <rect x="25" y="38" width="14" height="11" rx="2" fill="#ffd166" stroke="#8a5a10" stroke-width="1.5"/>
              <circle cx="32" cy="42" r="1.6" fill="#5b2e08"/>
              <path d="M48 13l1 3 3 1-3 1-1 3-1-3-3-1 3-1z" fill="#ffe08a"/>
              <path d="M18 27c2-5 7-7 12-7" fill="none" stroke="#fff3c4" stroke-width="2" stroke-linecap="round" opacity=".55"/>
            </symbol>
            <symbol id="ccf-mini" viewBox="0 0 32 32">
              <path d="M4 14c0-5 4-8 9-8h6c5 0 9 3 9 8v2H4z" fill="currentColor" opacity=".55"/>
              <rect x="4" y="16" width="24" height="12" rx="3" fill="currentColor"/>
              <rect x="13" y="15" width="6" height="13" fill="#000" opacity=".22"/>
              <rect x="13.5" y="19" width="5" height="5" rx="1" fill="#000" opacity=".24"/>
            </symbol>
            <symbol id="ic-home" viewBox="0 0 24 24"><path d="M3 10.5 12 3l9 7.5"/><path d="M5.5 9.5V21h13V9.5"/><path d="M10 21v-6h4v6"/></symbol>
            <symbol id="ic-chart" viewBox="0 0 24 24"><path d="M5 20V11M12 20V4M19 20v-7"/></symbol>
            <symbol id="ic-settings" viewBox="0 0 24 24"><path d="M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z"/><circle cx="12" cy="12" r="3"/></symbol>
            <symbol id="ic-cal" viewBox="0 0 24 24"><rect x="3.5" y="5" width="17" height="15.5" rx="3"/><path d="M8 3v4M16 3v4M3.5 10h17"/><path d="M8 14h2M14 14h2M8 17.5h2M14 17.5h2"/></symbol>
            <symbol id="ic-chev" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></symbol>
            <symbol id="ic-arrow" viewBox="0 0 24 24"><path d="M5 12h14M13 6l6 6-6 6"/></symbol>
            <symbol id="ic-trend" viewBox="0 0 24 24"><path d="M4 17 10 11l4 4 6-8"/><path d="M15 7h5v5"/></symbol>
            <symbol id="ic-clock" viewBox="0 0 24 24"><circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/></symbol>
            <symbol id="ic-pie" viewBox="0 0 24 24"><path d="M12 3a9 9 0 1 0 9 9h-9z"/><path d="M14 3.3A9 9 0 0 1 20.7 10H14z"/></symbol>
            <symbol id="ic-info" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/></symbol>
            <symbol id="ic-moon" viewBox="0 0 24 24"><path d="M20.5 14.5A8.5 8.5 0 0 1 9.5 3.5a8.5 8.5 0 1 0 11 11z"/></symbol>
            <symbol id="ic-sun" viewBox="0 0 24 24"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5 19 19M19 5l-1.5 1.5M6.5 17.5 5 19"/></symbol>
            <symbol id="ic-close" viewBox="0 0 24 24"><path d="m6 6 12 12M18 6 6 18"/></symbol>
            <symbol id="ic-refresh" viewBox="0 0 24 24"><path d="M20 11a8 8 0 1 0-2.3 6.7"/><path d="M20 5v6h-6"/></symbol>
          </defs>
        </svg>
        """;

    private const string CastleArt = """
        <svg class="castle" viewBox="0 0 264 320" preserveAspectRatio="xMidYMax slice" aria-hidden="true">
          <defs>
            <radialGradient id="cGlow" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="#7c3aed" stop-opacity=".38"/><stop offset="1" stop-color="#7c3aed" stop-opacity="0"/></radialGradient>
          </defs>
          <circle cx="120" cy="210" r="155" fill="url(#cGlow)"/>
          <path d="M0 302 L38 252 L88 292 L142 228 L198 282 L264 240 V320 H0Z" fill="#180a2e"/>
          <g fill="#120722">
            <path d="M8 268l14-38 14 38zM24 250h12v60H24zM210 262l13-35 13 35zM224 246h10v64h-10z"/>
            <path d="M56 280l9-24 9 24zM66 270h8v42h-8z"/>
          </g>
          <g fill="#1f0c3b">
            <rect x="72" y="206" width="112" height="88"/>
            <rect x="58" y="180" width="26" height="114"/><path d="M54 180h34l-17-26z"/>
            <rect x="176" y="164" width="32" height="130"/><path d="M172 164h40l-20-29z"/>
            <rect x="112" y="150" width="34" height="144"/><path d="M108 150h42l-21-31z"/>
          </g>
          <g fill="#3f1f70" opacity=".85">
            <rect x="68" y="220" width="6" height="12"/><rect x="86" y="220" width="6" height="12"/><rect x="104" y="220" width="6" height="12"/><rect x="122" y="220" width="6" height="12"/><rect x="140" y="220" width="6" height="12"/><rect x="158" y="220" width="6" height="12"/><rect x="176" y="220" width="6" height="12"/>
            <rect x="65" y="192" width="4" height="9"/><rect x="184" y="178" width="4" height="9"/><rect x="120" y="166" width="4" height="9"/>
          </g>
          <g fill="#0c0518">
            <path d="M0 300l16-36 16 36zM22 286h10v34H22zM228 292l15-34 15 34zM240 280h8v40h-8z"/>
          </g>
        </svg>
        """;

    private const string HeroArt = """
        <svg class="hero-art" viewBox="0 0 700 360" preserveAspectRatio="xMidYMax meet" aria-hidden="true">
          <defs>
            <radialGradient id="hGlow" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="#fbbf24" stop-opacity=".5"/><stop offset=".45" stop-color="#a855f7" stop-opacity=".3"/><stop offset="1" stop-color="#a855f7" stop-opacity="0"/></radialGradient>
            <linearGradient id="hGold" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffe9a8"/><stop offset=".4" stop-color="#f6b73c"/><stop offset=".78" stop-color="#d97706"/><stop offset="1" stop-color="#9a4a08"/></linearGradient>
            <linearGradient id="hGoldDark" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#b45309"/><stop offset="1" stop-color="#6b2e05"/></linearGradient>
            <linearGradient id="hBlue" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#a5d0ff"/><stop offset=".5" stop-color="#3b82f6"/><stop offset="1" stop-color="#1d4ed8"/></linearGradient>
            <filter id="hBlur" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="7"/></filter>
            <filter id="hSoft"><feGaussianBlur stdDeviation="10"/></filter>
          </defs>
          <circle cx="470" cy="170" r="215" fill="url(#hGlow)"/>
          <path d="M0 330 L70 268 L130 306 L205 228 L280 306 L360 250 L440 312 L520 268 L610 330 L700 292 V360 H0Z" fill="#1c0a36"/>
          <path d="M0 344 L95 294 L180 334 L268 282 L360 338 L455 300 L550 340 L640 306 L700 330 V360 H0Z" fill="#100620"/>
          <g fill="#0b0416">
            <path d="M30 330l16-42 16 42zM48 312h12v48H48z"/>
            <path d="M95 336l13-34 13 34zM110 320h9v46h-9z"/>
            <path d="M615 330l15-38 15 38zM632 314h10v46h-10z"/>
            <path d="M660 340l12-30 12 30zM675 328h8v32h-8z"/>
          </g>
          <g transform="translate(300 52) scale(1.06)">
            <ellipse cx="150" cy="272" rx="152" ry="24" fill="#0b0416" opacity=".75" filter="url(#hSoft)"/>
            <path d="M22 120 Q22 78 62 78 H238 Q278 78 278 120 V250 Q278 268 258 268 H42 Q22 268 22 250Z" fill="url(#hGoldDark)" stroke="#552604" stroke-width="5"/>
            <path d="M22 132 V108 Q22 52 84 52 H216 Q278 52 278 108 V132 Z" fill="url(#hGold)" stroke="#552604" stroke-width="5"/>
            <path d="M50 102 Q64 66 112 63" fill="none" stroke="#fff3c4" stroke-width="8" stroke-linecap="round" opacity=".55"/>
            <path d="M22 158 H278 V250 Q278 268 258 268 H42 Q22 268 22 250Z" fill="#7c3a0a" opacity=".32"/>
            <rect x="64" y="52" width="32" height="216" rx="7" fill="url(#hBlue)" stroke="#1e3a8a" stroke-width="4"/>
            <rect x="204" y="52" width="32" height="216" rx="7" fill="url(#hBlue)" stroke="#1e3a8a" stroke-width="4"/>
            <rect x="22" y="112" width="256" height="28" fill="url(#hBlue)" stroke="#1e3a8a" stroke-width="4"/>
            <rect x="119" y="142" width="62" height="68" rx="12" fill="url(#hGold)" stroke="#7c2d12" stroke-width="5"/>
            <circle cx="150" cy="168" r="9" fill="#5b1e08"/>
            <path d="M150 173v20" stroke="#5b1e08" stroke-width="7" stroke-linecap="round"/>
            <circle cx="44" cy="92" r="5" fill="#ffe8b0"/><circle cx="256" cy="92" r="5" fill="#ffe8b0"/>
            <circle cx="44" cy="246" r="5" fill="#ffe8b0"/><circle cx="256" cy="246" r="5" fill="#ffe8b0"/>
            <rect x="27" y="123" width="246" height="8" fill="#ffe08a" opacity=".8" filter="url(#hBlur)"/>
            <rect x="40" y="86" width="46" height="7" rx="3" fill="#fff3c4" opacity=".35"/>
          </g>
          <g fill="#ffe9a8">
            <path d="M286 112l2 6 6 2-6 2-2 6-2-6-6-2 6-2z" opacity=".8"/>
            <path d="M604 118l1.5 4.5 4.5 1.5-4.5 1.5-1.5 4.5-1.5-4.5-4.5-1.5 4.5-1.5z" opacity=".65"/>
            <circle cx="254" cy="170" r="2.5" opacity=".5"/><circle cx="574" cy="86" r="2" opacity=".5"/><circle cx="630" cy="210" r="2.5" opacity=".4"/>
          </g>
        </svg>
        """;

    private const string BalloonArt = """
        <svg class="balloon" viewBox="0 0 300 220" aria-hidden="true">
          <g fill="#100522" opacity=".78">
            <path d="M150 12c-33 0-56 28-56 62 0 30 22 54 56 82 34-28 56-52 56-82 0-34-23-62-56-62z"/>
            <path d="M112 142l38 24 38-24" fill="none" stroke="#16082e" stroke-width="3"/>
            <rect x="130" y="164" width="40" height="26" rx="4"/>
            <path d="M0 220l40-14 30 8 28-14 26 12 34-20 30 14 38-8 34 12 40-6v16H0z"/>
            <rect x="178" y="186" width="8" height="34"/><rect x="198" y="176" width="6" height="44"/><rect x="214" y="190" width="7" height="30"/>
          </g>
        </svg>
        """;

    private const string Style = """
        :root{
          --bg:#0a0616; --bg2:#0d0820; --panel:#171029; --panel2:#1d1433; --side:#0b0618;
          --line:rgba(255,255,255,.075); --ink:#f5f0ff; --ink2:#fff; --muted:#a89bc2; --muted2:#807197;
          --purple:#8b5cf6; --purple2:#a78bfa; --purple3:#6d28d9; --gold:#f6b73c; --green:#4ade80;
          --track:rgba(255,255,255,.075); --grid:rgba(255,255,255,.075); --stripe:rgba(255,255,255,.022);
          --radius:24px; --shadow:0 32px 80px -55px #000;
        }
        html[data-theme="light"]{
          --bg:#f4f0fa; --bg2:#f1ebfa; --panel:#fff; --panel2:#fbf9ff;
          --line:#e7dff2; --ink:#241636; --ink2:#150c24; --muted:#6c5d83; --muted2:#9688aa;
          --purple:#7c3aed; --purple2:#6d28d9; --purple3:#5b21b6; --track:#ece5f6; --grid:#e9e1f2;
          --stripe:rgba(45,20,80,.027); --shadow:0 30px 70px -50px rgba(40,20,80,.35);
        }
        *{box-sizing:border-box}
        html{scroll-behavior:smooth}
        body{
          margin:0; min-height:100vh; color:var(--ink); font-family:"Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,sans-serif;
          background:radial-gradient(1100px 800px at 78% -8%,rgba(124,58,237,.16),transparent 62%),
                     radial-gradient(900px 700px at 6% 108%,rgba(76,29,149,.2),transparent 60%),var(--bg);
          -webkit-font-smoothing:antialiased;
        }
        button{font:inherit;color:inherit}
        a{color:inherit;text-decoration:none}
        svg{display:block}
        .sprite{position:absolute;width:0;height:0;overflow:hidden}
        .ico{width:20px;height:20px;flex:none;fill:none;stroke:currentColor;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
        .num,.hero-value,.type-count,.count-cell,.total-value{font-variant-numeric:tabular-nums}
        #stats{scroll-margin-top:18px}
        :focus-visible{outline:2px solid var(--purple2);outline-offset:3px}
        .side{
          position:fixed;left:0;top:0;bottom:0;width:264px;z-index:30;display:flex;flex-direction:column;overflow:hidden;
          background:linear-gradient(180deg,#0e0820 0%,#0a0516 52%,#0b0519 100%);border-right:1px solid rgba(255,255,255,.06);
        }
        .side-glow{position:absolute;inset:-80px 0 auto 0;height:420px;background:radial-gradient(280px 260px at 50% 20%,rgba(139,92,246,.28),transparent 70%);pointer-events:none}
        .brand{position:relative;z-index:2;display:flex;flex-direction:column;align-items:center;gap:8px;padding:28px 18px 12px;text-align:center}
        .brand-logo{width:82px;height:82px;filter:drop-shadow(0 0 28px rgba(139,92,246,.5)) drop-shadow(0 12px 20px rgba(0,0,0,.45))}
        .brand-logo svg{width:100%;height:100%}
        .brand-name{font-size:19px;font-weight:800;line-height:1.14;letter-spacing:-.3px;color:#fff}
        .brand-name em{display:block;font-style:normal;color:#a78bfa}
        .side-profile{position:relative;z-index:2;display:flex;align-items:center;justify-content:center;gap:6px;margin:4px 0 14px;font-size:11.5px;color:#a99cc2;text-align:center;padding:0 16px}
        .side-dot{width:6px;height:6px;border-radius:50%;background:#4ade80;box-shadow:0 0 8px rgba(74,222,128,.9);flex:none}
        .nav{position:relative;z-index:2;display:flex;flex-direction:column;gap:6px;padding:0 16px}
        .nav-item{display:flex;align-items:center;gap:12px;height:47px;padding:0 16px;border-radius:13px;font-size:14.5px;font-weight:600;color:#a99cc2;background:transparent;border:0;cursor:pointer;text-align:left;transition:background .16s,color .16s,box-shadow .16s;width:100%}
        .nav-item:hover{background:rgba(255,255,255,.055);color:#fff}
        .nav-item.active{background:linear-gradient(135deg,#8b5cf6,#6d28d9);color:#fff;box-shadow:0 12px 26px -12px rgba(124,58,237,.95)}
        .side-version{position:absolute;z-index:3;left:22px;bottom:14px;font-size:11px;color:#7f719b}
        .castle{position:absolute;z-index:1;left:0;bottom:0;width:100%;height:320px;opacity:.95;pointer-events:none}
        .side::after{content:"";position:absolute;z-index:2;left:0;right:0;bottom:0;height:96px;background:linear-gradient(180deg,transparent,rgba(7,3,14,.72));pointer-events:none}
        .main{margin-left:264px;display:flex;justify-content:center;min-height:100vh}
        .content{width:100%;max-width:1280px;padding:26px 34px 56px}
        .topbar{position:relative;z-index:20;display:flex;align-items:center;justify-content:flex-end;gap:12px;margin-bottom:20px}
        .status-wrap{position:relative}
        .status-pill{display:flex;align-items:center;gap:9px;height:40px;padding:0 14px;border-radius:999px;background:var(--panel);border:1px solid var(--line);color:var(--muted);font-size:12.5px;cursor:pointer;transition:border-color .15s,color .15s;white-space:nowrap}
        .status-pill:hover{border-color:rgba(139,92,246,.55);color:var(--ink)}
        .dot{width:8px;height:8px;border-radius:50%;background:#4ade80;box-shadow:0 0 9px rgba(74,222,128,.85);flex:none}
        .status-pill .chev{width:15px;height:15px;margin-left:2px;opacity:.8}
        .round{width:40px;height:40px;border-radius:50%;display:grid;place-items:center;background:var(--panel);border:1px solid var(--line);color:var(--muted);cursor:pointer;transition:border-color .15s,color .15s,transform .15s;flex:none}
        .round:hover{border-color:rgba(139,92,246,.55);color:var(--ink);transform:translateY(-1px)}
        .round.small{width:34px;height:34px}
        .pop{display:none;position:absolute;right:0;top:50px;width:336px;padding:16px;border-radius:16px;background:var(--panel);border:1px solid var(--line);box-shadow:0 30px 70px -24px #000;z-index:40}
        .pop.open{display:block}
        .pop-head{display:flex;align-items:center;justify-content:space-between;font-size:13px;color:var(--ink);margin-bottom:4px}
        .pop-head span{font-size:11.5px;color:var(--muted2)}
        .pop-row{display:flex;justify-content:space-between;gap:16px;padding:9px 0;border-top:1px solid var(--line);font-size:12px;line-height:1.45}
        .pop-row span{color:var(--muted2);flex:none}
        .pop-row b{font-weight:600;color:var(--ink);text-align:right;overflow-wrap:anywhere}
        .pop-refresh{display:flex;align-items:center;justify-content:center;gap:8px;width:100%;height:36px;margin-top:10px;border:0;border-radius:9px;background:linear-gradient(135deg,#8b5cf6,#6d28d9);color:#fff;font-size:12.5px;font-weight:600;cursor:pointer}
        .pop-refresh .ico{width:15px;height:15px}
        .pop-note{margin-top:9px;font-size:10.5px;line-height:1.45;color:var(--muted2)}
        .hero{position:relative;overflow:hidden;min-height:308px;display:flex;align-items:center;padding:32px 40px;border-radius:26px;border:1px solid rgba(167,139,250,.38);
          background:radial-gradient(900px 520px at 82% 8%,rgba(168,85,247,.42),transparent 62%),linear-gradient(115deg,#4c1d95 0%,#341168 46%,#1b0a3a 100%);
          box-shadow:0 36px 90px -55px rgba(76,29,149,1);animation:rise .55s cubic-bezier(.2,.7,.2,1) both}          .hero-art{position:absolute;right:18px;bottom:-8px;width:55%;height:100%;opacity:.98;pointer-events:none;filter:drop-shadow(0 24px 36px rgba(0,0,0,.3))}
        .hero-inner{position:relative;z-index:2;max-width:58%}
        .hero-label{display:flex;align-items:center;gap:14px}
        .hero-badge{width:56px;height:56px;border-radius:16px;display:grid;place-items:center;background:linear-gradient(160deg,rgba(139,92,246,.55),rgba(76,29,149,.7));border:1px solid rgba(196,181,253,.4);box-shadow:inset 0 1px 0 rgba(255,255,255,.22),0 12px 28px -12px #000;flex:none}
        .hero-badge svg{width:38px;height:38px}
        .hero h1{margin:0;font-size:27px;font-weight:600;letter-spacing:-.2px;color:#fff}
        .hero-value{margin:8px 0 12px;font-size:clamp(74px,8.7vw,116px);font-weight:800;line-height:.9;letter-spacing:-.045em;color:#fff;text-shadow:0 20px 60px rgba(0,0,0,.35)}
        .hero-trend{display:flex;align-items:center;gap:8px;font-size:15px;font-weight:600;color:#86efac}
        .hero-trend .ico{width:18px;height:18px}
        .hero-trend.neg{color:#fca5a5}
        .hero-note{margin-top:7px;font-size:12.5px;color:rgba(255,255,255,.62)}
        .card{background:linear-gradient(180deg,var(--panel2),var(--panel));border:1px solid var(--line);border-radius:var(--radius);box-shadow:var(--shadow)}
        .chart-card{margin-top:22px;padding:24px 26px 18px;animation:rise .55s .06s cubic-bezier(.2,.7,.2,1) both}
        .card-head{display:flex;align-items:center;justify-content:space-between;gap:16px;flex-wrap:wrap}
        .card-title{display:flex;align-items:center;gap:12px}
        .tile{width:38px;height:38px;border-radius:11px;display:grid;place-items:center;background:linear-gradient(160deg,rgba(139,92,246,.28),rgba(76,29,149,.16));border:1px solid rgba(167,139,250,.24);color:var(--purple2);flex:none}
        .tile .ico{width:20px;height:20px}
        .card-title h2,.total-head h2{margin:0;font-size:19px;font-weight:700;letter-spacing:-.2px;color:var(--ink2)}
        .periods{display:flex;gap:4px;padding:4px;border-radius:12px;background:var(--track)}
        .period{height:32px;min-width:48px;padding:0 13px;border:0;border-radius:9px;background:transparent;color:var(--muted);font-size:12.5px;font-weight:600;cursor:pointer;transition:background .15s,color .15s,box-shadow .15s}
        .period.active{background:linear-gradient(135deg,#8b5cf6,#6d28d9);color:#fff;box-shadow:0 7px 18px -8px rgba(124,58,237,.95)}
        .period:hover:not(.active){background:rgba(255,255,255,.06);color:var(--ink)}
        .chart{display:grid;grid-template-columns:48px 1fr;gap:8px;margin-top:20px}
        .yaxis{height:280px;display:flex;flex-direction:column;justify-content:space-between;text-align:right;font-size:12px;color:var(--muted2);padding:0 2px}
        .plot{position:relative;height:280px}
        .gridlines{position:absolute;inset:0 6px}
        .gridlines i{position:absolute;left:0;right:0;height:1px;background:var(--grid)}
        .gridlines i:nth-child(1){top:0}.gridlines i:nth-child(2){top:33.33%}.gridlines i:nth-child(3){top:66.66%}.gridlines i:nth-child(4){top:100%}
        .bars{position:absolute;inset:0 6px;display:flex;align-items:flex-end;gap:16px;padding-top:6px}
        .col{flex:1;height:100%;display:flex;align-items:flex-end;justify-content:center;min-width:0}
        .bar{display:block;width:100%;max-width:52px;height:calc(var(--h) * 1%);border-radius:9px 9px 3px 3px;background:linear-gradient(180deg,#c4b5fd,#8b5cf6 58%,#6d28d9);box-shadow:0 0 20px rgba(139,92,246,.3);transform-origin:bottom;animation:bar .55s cubic-bezier(.2,.7,.2,1) both;animation-delay:calc(var(--i) * 16ms)}
        .bar:hover{filter:brightness(1.14)}
        .bar.zero{height:2px!important;border-radius:2px;background:var(--track);box-shadow:none}
        .xaxis{grid-column:2;display:flex;gap:16px;margin-top:10px;font-size:11.5px;color:var(--muted2)}
        .xaxis span{flex:1;text-align:center;white-space:nowrap;overflow:hidden}
        .xaxis span.minor{visibility:hidden}
        .chart.wide .bars,.chart.wide .xaxis{gap:16px}
        .chart.dense .bars,.chart.dense .xaxis{gap:5px}
        .chart.ultra .bars,.chart.ultra .xaxis{gap:3px}
        .chart-empty{grid-column:1/-1;padding:22px 0 26px;text-align:center;font-size:13px;color:var(--muted2)}
        .chart-note{margin-top:14px;padding-top:12px;border-top:1px solid var(--line);font-size:11.5px;line-height:1.5;color:var(--muted2)}
        .two{display:grid;grid-template-columns:1.16fr .84fr;gap:22px;margin-top:22px}
        .type-card,.total-card{padding:24px 26px;animation:rise .55s .1s cubic-bezier(.2,.7,.2,1) both}
        .type-rows{margin-top:8px}
        .type-row{display:grid;grid-template-columns:34px minmax(164px,1.35fr) minmax(90px,1.45fr) 52px 50px;gap:12px;align-items:center;padding:11px 0}
        .type-ico{width:30px;height:30px;display:grid;place-items:center}
        .type-ico svg{width:30px;height:30px}
        .type-name{font-size:14px;font-weight:500;color:var(--ink);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .type-track{height:9px;border-radius:99px;background:var(--track);overflow:hidden}
        .type-track i{display:block;height:9px;border-radius:99px}
        .type-count{text-align:right;font-size:16px;font-weight:700;color:var(--ink2)}
        .type-pct{text-align:right;font-size:12px;color:var(--muted2);white-space:nowrap}
        .card-note{margin-top:14px;padding-top:12px;border-top:1px solid var(--line);font-size:11.5px;line-height:1.5;color:var(--muted2)}
        .total-card{position:relative;overflow:hidden;border-radius:var(--radius);border:1px solid rgba(167,139,250,.32);
          background:radial-gradient(420px 300px at 86% 18%,rgba(168,85,247,.32),transparent 72%),linear-gradient(150deg,#3b1179,#2a0b59 55%,#1a0738);box-shadow:0 32px 80px -55px #000;color:#fff}
        .total-head{display:flex;align-items:center;gap:10px;font-size:15px;font-weight:600;color:rgba(255,255,255,.92)}
        .total-head .tile{width:32px;height:32px;border-radius:9px;background:rgba(255,255,255,.12);border:1px solid rgba(255,255,255,.22);color:#fff}
        .total-head h2{font-size:15px;font-weight:600;color:rgba(255,255,255,.92)}
        .total-value{margin:18px 0 6px;font-size:clamp(58px,6vw,84px);font-weight:800;line-height:1;letter-spacing:-.035em;color:#fff}
        .total-sub{font-size:14px;color:rgba(255,255,255,.76)}
        .total-meta{margin-top:14px;font-size:12px;color:rgba(255,255,255,.55)}
        .balloon{position:absolute;right:-14px;bottom:-14px;width:58%;opacity:.72;pointer-events:none}
        .matches{margin-top:22px;padding:24px 26px 10px;animation:rise .55s .14s cubic-bezier(.2,.7,.2,1) both}
        .matches .card-head{margin-bottom:10px}
        .table-wrap{overflow-x:auto}
        table{width:100%;border-collapse:collapse}
        th{font-size:10.5px;letter-spacing:.12em;text-transform:uppercase;font-weight:600;color:var(--muted2);text-align:left;padding:12px;border-bottom:1px solid var(--line)}
        th.right{text-align:right}
        td{padding:11px 12px;border-bottom:1px solid var(--line);vertical-align:middle}
        tbody tr:last-child td{border-bottom:0}
        tbody tr:nth-child(even){background:var(--stripe)}
        tbody tr:hover{background:rgba(139,92,246,.07)}
        .date-cell{display:flex;align-items:center;gap:11px;min-width:0}
        .cal{width:17px;height:17px;color:var(--purple2);flex:none}
        .date-main{display:block;font-size:14px;font-weight:600;color:var(--ink)}
        .match-sub{display:block;margin-top:2px;max-width:620px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:11.5px;color:var(--muted2)}
        .count-cell{text-align:right;font-size:18px;font-weight:700;color:var(--ink2);white-space:nowrap}
        .empty{padding:30px 12px!important;text-align:center!important;font-size:13px;color:var(--muted2)}
        .btn-more{display:inline-flex;align-items:center;gap:8px;height:34px;padding:0 14px;border-radius:999px;border:1px solid var(--line);background:transparent;color:var(--muted);font-size:12.5px;font-weight:600;cursor:pointer;transition:color .15s,border-color .15s,background .15s}
        .btn-more:hover{color:var(--ink);border-color:rgba(139,92,246,.55);background:rgba(139,92,246,.08)}
        .btn-more .ico{width:15px;height:15px;transition:transform .2s}
        .btn-more.less .ico{transform:rotate(-90deg)}
        [hidden]{display:none!important}
        footer{margin-top:28px;padding-top:18px;border-top:1px solid var(--line);display:flex;flex-direction:column;gap:8px;font-size:11.5px;line-height:1.65;color:var(--muted2)}
        .foot-brand{display:flex;flex-wrap:wrap;gap:10px;align-items:baseline;color:var(--muted)}
        .foot-brand b{color:var(--ink);font-weight:700}
        footer p{margin:0;max-width:940px}
        .loading{display:flex;align-items:center;gap:16px;padding:18px 22px;margin-bottom:18px}
        .spinner{width:30px;height:30px;flex:none;border-radius:50%;border:3px solid var(--track);border-top-color:var(--purple2);animation:spin .9s linear infinite}
        .load-body{flex:1;min-width:0;font-size:12.5px;color:var(--muted)}
        .load-body b{display:block;color:var(--ink);font-size:13px;margin-bottom:7px}
        .load-body span{font-size:11px;color:var(--muted2)}
        .pbar{height:6px;border-radius:99px;background:var(--track);overflow:hidden;margin-bottom:6px}
        .pbar i{display:block;height:6px;border-radius:99px;background:linear-gradient(90deg,#8b5cf6,#c4b5fd)}
        .modal{position:fixed;inset:0;z-index:100;display:none;align-items:center;justify-content:center;padding:20px;background:rgba(5,2,10,.66);backdrop-filter:blur(5px)}
        .modal.open{display:flex}
        .modal-card{width:min(580px,100%);max-height:90vh;overflow:auto;padding:26px;border-radius:22px;background:var(--panel);border:1px solid var(--line);box-shadow:0 40px 110px -30px #000;animation:pop .2s ease both}
        .modal-head{display:flex;align-items:center;justify-content:space-between;gap:14px}
        .modal-head h2{margin:0;font-size:22px;color:var(--ink2);letter-spacing:-.3px}
        .modal-sub{margin:6px 0 16px;font-size:12.5px;line-height:1.5;color:var(--muted2)}
        .set-row{display:grid;grid-template-columns:154px 1fr;gap:14px;padding:10px 0;border-top:1px solid var(--line);font-size:12.5px}
        .set-row span{color:var(--muted2)}
        .set-row b{font-weight:600;color:var(--ink);overflow-wrap:anywhere}
        .btn-primary{width:100%;height:40px;margin-top:18px;border:0;border-radius:10px;background:linear-gradient(135deg,#8b5cf6,#6d28d9);color:#fff;font-size:13px;font-weight:600;cursor:pointer}
        body.modal-open{overflow:hidden}
        .hero,.chart-card,.two>section,.matches{will-change:transform,opacity}
        @keyframes rise{from{opacity:0;transform:translateY(14px)}to{opacity:1;transform:none}}
        @keyframes bar{from{transform:scaleY(0)}to{transform:scaleY(1)}}
        @keyframes spin{to{transform:rotate(360deg)}}
        @keyframes pop{from{opacity:0;transform:translateY(8px) scale(.99)}to{opacity:1;transform:none}}
        @media (max-width:1180px){
          .side{width:228px}
          .main{margin-left:228px}
          .hero-value{font-size:96px}
          .hero-art{width:62%;opacity:.9}
          .two{grid-template-columns:1fr 1fr}
          .type-row{grid-template-columns:30px minmax(150px,1.3fr) minmax(80px,1.35fr) 46px 46px;gap:10px}
        }
        @media (max-width:980px){
          .hero-inner{max-width:66%}
          .hero-art{width:52%;opacity:.8}
          .two{grid-template-columns:1fr}
          .type-row{grid-template-columns:34px minmax(160px,1.25fr) minmax(110px,1.45fr) 56px 50px;gap:12px}
        }
        @media (max-width:860px){
          .side{position:relative;width:auto;height:auto;flex-direction:row;align-items:center;gap:8px;padding:12px 14px;overflow:visible;border-right:0;border-bottom:1px solid rgba(255,255,255,.08)}
          .side-glow,.castle,.side-version{display:none}
          .brand{flex-direction:row;padding:0;gap:10px;text-align:left;flex:none}
          .brand-logo{width:48px;height:48px}
          .brand-name{font-size:14px;line-height:1.05}
          .brand-name em{display:inline;margin-left:4px}
          .side-profile{display:none}
          .nav{flex-direction:row;gap:4px;margin-left:auto;padding:0}
          .nav-item{height:40px;padding:0 12px;font-size:12.5px;gap:7px;width:auto}
          .nav-item .ico{width:17px;height:17px}
          .main{margin-left:0}
          .content{padding:18px 16px 40px}
          .topbar{margin-bottom:14px}
          .status-pill{height:36px;padding:0 11px;font-size:11.5px}
          .status-pill #statusText{max-width:160px;overflow:hidden;text-overflow:ellipsis}
          .round{width:36px;height:36px}
          .hero{min-height:280px;padding:24px;border-radius:20px}
          .hero-art{width:70%;right:-40px;opacity:.55}
          .hero-inner{max-width:100%}
          .hero-badge{width:46px;height:46px}.hero-badge svg{width:31px;height:31px}
          .hero h1{font-size:21px}
          .hero-value{font-size:76px}
          .hero-trend{font-size:13px}
          .chart-card,.type-card,.total-card,.matches{padding:18px;border-radius:20px}
          .card-title h2{font-size:17px}
          .periods{width:100%;justify-content:space-between}
          .period{flex:1;min-width:0}
          .chart{margin-top:16px}
          .yaxis,.plot{height:220px}
          .xaxis{gap:0!important;font-size:10px}
          .chart.wide .bars{gap:8px}
          .chart.wide .xaxis{gap:8px}
          .pop{position:fixed;left:16px;right:16px;top:70px;width:auto}
          .match-sub{max-width:250px}
          th,td{padding:10px 8px}
          .date-main{font-size:13px}
          .count-cell{font-size:16px}
          .type-row{grid-template-columns:30px minmax(140px,1.15fr) minmax(70px,1.15fr) 44px 42px;gap:9px}
          .type-name{font-size:13px}.type-count{font-size:14px}.type-pct{font-size:11px}
          .set-row{grid-template-columns:110px 1fr;gap:10px}
        }
        @media (max-width:560px){
          .brand-name{display:none}
          .nav-item{padding:0 9px}
          .nav-item .ico{margin:0}
          .nav-item{font-size:0;gap:0}
          .nav-item .ico{width:19px;height:19px}
          .hero-art{display:none}
          .hero-value{font-size:68px}
          .hero{padding:22px}
          .two{gap:14px}
          .card-head{gap:10px}
          .periods{margin-top:6px}
          .match-sub{max-width:180px}
          .type-row{grid-template-columns:26px 1fr 44px;gap:10px}
          .type-track{grid-column:1/-1;grid-row:2}
          .type-pct{display:none}
          .type-row{padding:9px 0}
          .total-value{font-size:64px}
          .chart{grid-template-columns:34px 1fr}
          .yaxis{font-size:10px}
        }
        @media (prefers-reduced-motion:reduce){
          html{scroll-behavior:auto}
          *,*::before,*::after{animation:none!important;transition:none!important}
        }
        """;

    private const string Script = """
        (() => {
          "use strict";
          const data = window.CCF_DATA || { days: [], start: "", end: "", lang: "fr", generatedAt: new Date().toISOString() };
          const txt = window.CCF_TXT || {};
          const root = document.documentElement;
          const $ = (id) => document.getElementById(id);
          const locale = data.lang === "fr" ? "fr-FR" : data.lang === "es" ? "es-ES" : "en-GB";
          const nf = new Intl.NumberFormat(locale);
          const df = new Intl.DateTimeFormat(locale, { day: "2-digit", month: "2-digit" });
          const dfFull = new Intl.DateTimeFormat(locale, { day: "2-digit", month: "2-digit", year: "numeric" });
          const dfMonth = new Intl.DateTimeFormat(locale, { month: "2-digit", year: "2-digit" });
          const DAY = 86400000;
          const parse = (s) => { const [y, m, d] = s.split("-").map(Number); return new Date(y, m - 1, d, 12); };
          const key = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
          const counts = new Map((data.days || []).map((x) => [x.d, x.c]));
          const end = data.end ? parse(data.end) : new Date();
          const start = data.start ? parse(data.start) : end;
          const chart = $("chart");
          const periodButtons = [...document.querySelectorAll(".period")];
          let period = "7";

          function buckets(p) {
            if (p === "all") {
              const span = Math.round((end - start) / DAY) + 1;
              if (span > 120) {
                const out = [];
                let cur = new Date(start.getFullYear(), start.getMonth(), 1, 12);
                const last = new Date(end.getFullYear(), end.getMonth(), 1, 12);
                while (cur <= last) {
                  let c = 0;
                  const y = cur.getFullYear(), m = cur.getMonth();
                  for (let d = 1; d <= 31; d++) {
                    const dt = new Date(y, m, d, 12);
                    if (dt.getMonth() !== m) break;
                    c += counts.get(key(dt)) || 0;
                  }
                  out.push({ label: dfMonth.format(cur), count: c, title: dfMonth.format(cur), major: true });
                  cur = new Date(y, m + 1, 1, 12);
                }
                return out;
              }
              const out = [];
              for (let i = 0; i < span; i++) {
                const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i, 12);
                out.push({ label: df.format(d), count: counts.get(key(d)) || 0, title: dfFull.format(d), major: span <= 14 || i % Math.ceil(span / 7) === 0 });
              }
              return out;
            }
            const days = p === "7" ? 7 : p === "30" ? 30 : 90;
            const step = days === 30 ? 5 : days === 90 ? 15 : 1;
            const out = [];
            for (let i = 0; i < days; i++) {
              const d = new Date(end.getFullYear(), end.getMonth(), end.getDate() - (days - 1) + i, 12);
              out.push({ label: df.format(d), count: counts.get(key(d)) || 0, title: dfFull.format(d), major: days === 7 || i % step === step - 1 });
            }
            return out;
          }

          function renderChart() {
            if (!chart) return;
            const b = buckets(period);
            const max = Math.max(1, ...b.map((x) => x.count));
            const top = Math.max(10, Math.ceil(max / 10) * 10);
            const ticks = [top, Math.round(top * 2 / 3), Math.round(top / 3), 0];
            const density = b.length <= 7 ? "wide" : b.length <= 31 ? "dense" : "ultra";
            const total = b.reduce((s, x) => s + x.count, 0);
            const bars = b.map((x, i) => {
              const h = x.count ? Math.max(3, (x.count / top) * 100) : 0;
              const title = `${x.title} · ${nf.format(x.count)} ${txt.chests || "coffres"}`;
              return `<div class="col" title="${title}"><span class="bar${x.count ? "" : " zero"}" style="--h:${h.toFixed(1)};--i:${i}"></span></div>`;
            }).join("");
            const labels = b.map((x) => `<span class="${x.major ? "" : "minor"}">${x.label}</span>`).join("");
            chart.className = `chart ${density}`;
            chart.innerHTML =
              `<div class="yaxis">${ticks.map((v) => `<span>${nf.format(v)}</span>`).join("")}</div>` +
              `<div class="plot"><div class="gridlines"><i></i><i></i><i></i><i></i></div><div class="bars">${bars}</div></div>` +
              `<div class="xaxis">${labels}</div>` +
              (total ? "" : `<div class="chart-empty">${txt.noChart || ""}</div>`);
            periodButtons.forEach((btn) => btn.classList.toggle("active", btn.dataset.period === period));
          }

          periodButtons.forEach((btn) => btn.addEventListener("click", () => {
            period = btn.dataset.period || "7";
            renderChart();
          }));
          renderChart();

          const heroValue = document.querySelector(".hero-value");
          if (heroValue) {
            const target = Number(heroValue.dataset.target || 0);
            const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
            if (target > 0 && !reduce) {
              const t0 = performance.now();
              const dur = 700;
              const tick = (t) => {
                const p = Math.min(1, (t - t0) / dur);
                const e = 1 - Math.pow(1 - p, 3);
                heroValue.textContent = nf.format(Math.round(target * e));
                if (p < 1) requestAnimationFrame(tick);
              };
              requestAnimationFrame(tick);
            }
          }

          const statusBtn = $("statusBtn"), statusPop = $("statusPop"), statusText = $("statusText");
          const generated = new Date(data.generatedAt || Date.now());
          function statusLabel() {
            const sec = Math.max(0, (Date.now() - generated.getTime()) / 1000);
            if (sec < 45) return txt.updatedJustNow || "";
            const ago = txt.updatedAgo || "{x}";
            if (sec < 90) return ago.replace("{x}", "1 " + (txt.minute || ""));
            const min = Math.floor(sec / 60);
            if (min < 60) return ago.replace("{x}", min + " " + (min > 1 ? txt.minutes : txt.minute));
            const h = Math.floor(min / 60);
            if (h < 24) return ago.replace("{x}", h + " " + (h > 1 ? txt.hours : txt.hour));
            const d = Math.floor(h / 24);
            return ago.replace("{x}", d + " " + (d > 1 ? txt.days : txt.day));
          }
          if (statusText) {
            statusText.textContent = statusLabel();
            window.setInterval(() => { statusText.textContent = statusLabel(); }, 20000);
          }
          function closePop() {
            statusPop?.classList.remove("open");
            statusBtn?.setAttribute("aria-expanded", "false");
          }
          statusBtn?.addEventListener("click", (e) => {
            e.stopPropagation();
            const open = statusPop?.classList.toggle("open");
            statusBtn.setAttribute("aria-expanded", open ? "true" : "false");
          });
          document.addEventListener("click", (e) => {
            if (!statusPop || !statusBtn) return;
            if (!statusPop.contains(e.target) && !statusBtn.contains(e.target)) closePop();
          });
          $("refreshBtn")?.addEventListener("click", () => window.location.reload());

          const themeBtn = $("themeBtn"), themeIcon = $("themeIcon");
          function paintTheme() {
            const light = root.dataset.theme === "light";
            themeIcon?.setAttribute("href", light ? "#ic-sun" : "#ic-moon");
            themeBtn?.setAttribute("aria-label", light ? (txt.themeToDark || "") : (txt.themeToLight || ""));
          }
          paintTheme();
          themeBtn?.addEventListener("click", () => {
            const light = root.dataset.theme === "light";
            root.dataset.theme = light ? "dark" : "light";
            try { localStorage.setItem("ccf-theme", root.dataset.theme); } catch (e) {}
            paintTheme();
          });

          const navItems = [...document.querySelectorAll(".nav-item")];
          navItems.forEach((item) => {
            if (!item.dataset.scroll) return;
            item.addEventListener("click", (e) => {
              e.preventDefault();
              const target = document.getElementById(item.dataset.scroll === "top" ? "top" : item.dataset.scroll);
              if (target) target.scrollIntoView({ behavior: "smooth", block: "start" });
              navItems.forEach((n) => n.classList.toggle("active", n === item));
            });
          });

          const modal = $("settingsModal");
          const openSettings = $("openSettings");
          const closeSettings = $("closeSettings");
          const closeSettings2 = $("closeSettings2");
          function openModal() {
            if (!modal) return;
            modal.classList.add("open");
            modal.setAttribute("aria-hidden", "false");
            document.body.classList.add("modal-open");
            closeSettings?.focus();
          }
          function closeModal() {
            if (!modal) return;
            modal.classList.remove("open");
            modal.setAttribute("aria-hidden", "true");
            document.body.classList.remove("modal-open");
          }
          openSettings?.addEventListener("click", openModal);
          closeSettings?.addEventListener("click", closeModal);
          closeSettings2?.addEventListener("click", closeModal);
          modal?.addEventListener("click", (e) => { if (e.target === modal) closeModal(); });
          document.addEventListener("keydown", (e) => {
            if (e.key !== "Escape") return;
            closeModal();
            closePop();
          });

          const seeAll = $("seeAll");
          if (seeAll) {
            const extras = [...document.querySelectorAll("tr.extra")];
            const label = seeAll.querySelector("span");
            seeAll.addEventListener("click", () => {
              const show = extras.length > 0 && extras[0].hidden;
              extras.forEach((row) => { row.hidden = !show; });
              if (label) label.textContent = show ? (txt.seeLess || "") : (txt.seeAll || "");
              seeAll.classList.toggle("less", show);
              seeAll.setAttribute("aria-expanded", show ? "true" : "false");
            });
          }
        })();
        """;
}
