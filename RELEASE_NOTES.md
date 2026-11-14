**1.5.1 — L'overlay s'adapte à ton stream**

**Français**
**Nouveautés**
- **Overlay auto-adaptatif** : la carte de stream et l'overlay de jeu s'adaptent maintenant à la taille de la source, sans redimensionnement manuel. La carte compacte monte jusqu'à 960 px de large (92 % de la source) et la grande carte garde une marge automatique.
- **Connexion au démarrage** : une page dédiée permet de se connecter avec une adresse e-mail et un mot de passe, ou avec Google. Le bouton « Plus tard, rester en local » permet de continuer sans compte.
- **Synchronisation multi-appareils** : l'historique et les statistiques sont associés au compte Supabase et récupérés sur les autres PC. Les parties sont fusionnées par replay : une partie analysée sur deux machines n'apparaît qu'une fois.
- **Totaux manuels synchronisés** : les coffres classiques, rares et d'esprit saisis dans « Totaux de saison » suivent le compte et sont restaurés sur les autres PC.
- **Réglages conservés** : l'overlay, la langue et le dossier des replays restent propres à chaque PC. Les fichiers `.replay` ne sont jamais envoyés.
- **Configuration intégrée** : la page de connexion permet de saisir l'URL du projet Supabase et sa clé publique. Le schéma SQL et le guide Google sont fournis dans `docs/SUPABASE.md`.
- **Overlay TikTok Studio corrigé** : le compteur fournit maintenant un lien HTTPS local, accepté par TikTok Studio (qui refuse les liens HTTP), sans tunnel ni certificat à installer.
- **Fonctions conservées** : carte violette en jeu, overlay de stream `/overlay/game`, grande carte OBS/TikTok, rapports, historique, archives ZIP, Discord et mises à jour automatiques.
- **Fermeture discrète** : la croix range le compteur dans la zone de notification et le laisse tourner en arrière-plan ; l'overlay reste la carte violette.

**English**
**What's new**
- **Self-sizing overlay**: the stream card and game overlay now fit the source size, with no manual resizing. The compact card grows up to 960 px wide (92% of the source) and the full card keeps automatic margins.
- **Sign-in at launch**: a dedicated page lets you use an email address and password, or Google. "Not now, stay local" keeps the app usable without an account.
- **Multi-device sync**: history and statistics are linked to the Supabase account and fetched on your other PCs. Matches are merged by replay, so one analyzed on two machines appears only once.
- **Manual totals sync**: classic, rare and sprite chests entered in "Season totals" follow the account and are restored on your other PCs.
- **Local settings stay local**: overlay, language and replay folder remain specific to each PC. `.replay` files are never uploaded.
- **Built-in setup**: the sign-in page accepts the Supabase project URL and public key. The SQL schema and Google guide are in `docs/SUPABASE.md`.
- **TikTok Studio overlay fixed**: the counter now serves a local HTTPS link accepted by TikTok Studio (which rejects HTTP links), with no tunnel or certificate to install.
- **Kept features**: purple in-game card, `/overlay/game` stream overlay, large OBS/TikTok card, reports, history, ZIP archives, Discord and automatic updates.
- **Quiet close**: the X button puts the counter in the notification area and keeps it running in the background; the overlay stays the purple card.

**Español**
**Novedades**
- **Overlay autoajustable**: la tarjeta del stream y el overlay de juego se adaptan ahora al tamaño de la fuente, sin redimensionarlos a mano. La tarjeta compacta llega hasta 960 px de ancho (92 % de la fuente) y la tarjeta grande conserva márgenes automáticos.
- **Conexión al iniciar**: una página dedicada permite entrar con correo y contraseña, o con Google. «Más tarde, seguir en local» mantiene la app usable sin cuenta.
- **Sincronización entre dispositivos**: el historial y las estadísticas se asocian a la cuenta de Supabase y se recuperan en los otros PC. Las partidas se fusionan por repetición: una analizada en dos máquinas aparece una sola vez.
- **Totales manuales sincronizados**: los cofres clásicos, raros y de espíritu introducidos en «Totales de temporada» siguen la cuenta y se restauran en los otros PC.
- **Ajustes locales**: el overlay, el idioma y la carpeta de repeticiones siguen siendo propios de cada PC. Los archivos `.replay` nunca se envían.
- **Configuración integrada**: la página de conexión acepta la URL del proyecto Supabase y su clave pública. El esquema SQL y la guía de Google están en `docs/SUPABASE.md`.
- **Overlay de TikTok Studio corregido**: el contador ofrece ahora un enlace HTTPS local que TikTok Studio acepta (rechaza los enlaces HTTP), sin túnel ni certificado que instalar.
- **Funciones conservadas**: tarjeta violeta en el juego, overlay de stream `/overlay/game`, tarjeta grande para OBS/TikTok, informes, historial, archivos ZIP, Discord y actualizaciones automáticas.
- **Cierre discreto**: la X pone el contador en el área de notificación y lo deja funcionando en segundo plano; el overlay sigue siendo la tarjeta violeta.

---

Windows peut afficher « Windows a protégé votre ordinateur » (exe non signé) : **Informations complémentaires → Exécuter quand même**. Téléchargement : https://github.com/emilien-atr/ChestCounterFortnite/releases/latest
