**1.6.1 — Migration forcée vers l’installateur**

**Français**
**Nouveautés**
- **Anciennes versions portables bloquées** : un exe portable encore lancé ne démarre plus normalement ; il ouvre automatiquement l’installateur officiel et installe la dernière version.
- **Installations propres** : les versions installées via le setup continuent de fonctionner et gardent leurs raccourcis, leur désinstallation et leurs données.
- **Même logique de mise à jour** : le binaire interne de l’auto-update reste utilisé pour les installations existantes, mais une copie portable est redirigée vers le setup.

**English**
**What's new**
- **Old portable builds blocked**: a portable exe that is still launched no longer starts normally; it opens the official installer and installs the latest version.
- **Clean installs**: builds installed with the setup keep working and retain their shortcuts, uninstall entry and data.
- **Same update flow**: the internal auto-update payload is still used for existing installs, while a portable copy is redirected to the setup.

**Español**
**Novedades**
- **Versiones portátiles antiguas bloqueadas**: un exe portátil que todavía se abra ya no arranca normalmente; abre el instalador oficial e instala la última versión.
- **Instalaciones limpias**: las versiones instaladas con el setup siguen funcionando y conservan sus accesos, su desinstalación y sus datos.
- **Misma lógica de actualización**: el binario interno del auto-update se sigue usando para las instalaciones existentes, mientras que una copia portátil se redirige al setup.

---

**1.6.0 — Installateur Windows séparé**

**Français**
**Nouveautés**
- **Installateur séparé** : `ChestCounterFortnite-Setup.exe` installe l’application pour ton compte dans `%LOCALAPPDATA%\Programs\ChestCounterFortnite`, sans droits administrateur.
- **Raccourcis gérés par l’installation** : le menu Démarrer et le raccourci Bureau sont créés par l’installateur ; l’application ne s’auto-installe plus au premier lancement.
- **Désinstallation propre** : ChestCounter apparaît dans « Applications installées ». Les données de `%APPDATA%\ChestCounterFortnite` et les replays sont conservés.
- **Mises à jour conservées** : l’auto-update continue de remplacer le binaire installé. Le site propose maintenant l’installateur, plus l’exe portable.

**English**
**What's new**
- **Separate installer**: `ChestCounterFortnite-Setup.exe` installs the app for your account in `%LOCALAPPDATA%\Programs\ChestCounterFortnite`, without administrator rights.
- **Installer-managed shortcuts**: the Start menu and desktop shortcuts are created by the installer; the app no longer self-installs on first launch.
- **Clean uninstall**: ChestCounter appears in “Installed apps”. Data in `%APPDATA%\ChestCounterFortnite` and replays are preserved.
- **Updates preserved**: the auto-updater still replaces the installed binary. The website now offers the installer, not the portable exe.

**Español**
**Novedades**
- **Instalador separado**: `ChestCounterFortnite-Setup.exe` instala la aplicación para tu cuenta en `%LOCALAPPDATA%\Programs\ChestCounterFortnite`, sin permisos de administrador.
- **Accesos gestionados por el instalador**: el menú Inicio y el acceso directo del escritorio los crea el instalador; la aplicación ya no se autoinstala en el primer inicio.
- **Desinstalación limpia**: ChestCounter aparece en «Aplicaciones instaladas». Los datos de `%APPDATA%\ChestCounterFortnite` y las repeticiones se conservan.
- **Actualizaciones conservadas**: el auto-update sigue reemplazando el binario instalado. La web ofrece ahora el instalador, no el exe portátil.

---

**1.5.5 — Nouveau coffre, overlay plus compact**

**Français**
**Nouveautés**
- **Nouveau coffre** : le coffre doré au crâne remplace l'ancien logo dans la fenêtre, l'overlay en jeu, le rapport, l'icône du programme et celle du site.
- **Overlay de stream plus compact** : la largeur maximale passe de 960 à 720 px, avec des chiffres et des marges ajustés pour rester bien lisibles.
- **Détourage transparent** : le fond vert de l'image a été retiré ; il ne reste que le coffre, ses chaînes et ses pierres, sans herbe ni décor.

**English**
**What's new**
- **New chest**: the golden skull chest replaces the old logo in the window, the in-game overlay, the report, the program icon and the website icon.
- **More compact stream overlay**: the maximum width goes from 960 to 720 px, with adjusted figures and margins that stay easy to read.
- **Transparent cut-out**: the green background is gone; only the chest, its chains and its gems remain, with no grass or scenery.

**Español**
**Novedades**
- **Nuevo cofre**: el cofre dorado con calavera sustituye al antiguo logo en la ventana, el overlay del juego, el informe, el icono del programa y el del sitio.
- **Overlay de stream más compacto**: el ancho máximo pasa de 960 a 720 px, con cifras y márgenes ajustados para seguir siendo legibles.
- **Recorte transparente**: se ha eliminado el fondo verde; solo quedan el cofre, sus cadenas y sus gemas, sin hierba ni decorado.

---

**1.5.4 — Configuration Supabase intégrée**

**Français**
**Nouveautés**
- **Configuration invisible** : l’URL et la clé publique du projet sont intégrées à l’application ; les utilisateurs ne peuvent plus les voir ni les modifier.
- **Fichier local nettoyé** : `supabase.json` ne contient plus l’URL ni la clé ; seul l’identifiant de l’appareil reste. La session reste dans `supabase-session.json`.
- **Sécurité** : la clé publishable ne donne aucun droit à elle seule ; les règles RLS protègent chaque compte. Ne jamais utiliser de clé `service_role` dans l’application.

**English**
**What's new**
- **Invisible configuration**: the project URL and publishable key are built into the app; users can no longer see or change them.
- **Clean local file**: `supabase.json` no longer contains the URL or key; only the device id remains. The session stays in `supabase-session.json`.
- **Security**: the publishable key grants no access by itself; RLS rules protect each account. Never use a `service_role` key in the app.

**Español**
**Novedades**
- **Configuración invisible**: la URL y la clave publishable del proyecto están integradas en la aplicación; los usuarios ya no pueden verlas ni cambiarlas.
- **Archivo local limpio**: `supabase.json` ya no contiene la URL ni la clave; solo queda el identificador del dispositivo. La sesión permanece en `supabase-session.json`.
- **Seguridad**: la clave publishable no da acceso por sí sola; las reglas RLS protegen cada cuenta. No uses nunca una clave `service_role` en la aplicación.

---

**1.5.3 — Connexion au compte obligatoire**

**Français**
**Nouveautés**
- **Option locale retirée** : le bouton « Plus tard, rester en local » a été supprimé. La connexion au compte est désormais nécessaire pour utiliser le compteur.
- **Lancement avec Fortnite** : l’écran de connexion apparaît aussi quand le compteur est démarré automatiquement avec le jeu ; l’application retourne dans la zone de notification après la connexion.
- **Mot de passe oublié** : un lien sur l'écran de connexion envoie un e-mail Supabase ; le retour dans l'application permet de choisir un nouveau mot de passe sans quitter ChestCounter.
- **Raccourci Bureau** : créé automatiquement au premier lancement de l’application.
- **Reste inchangé** : overlay, rapports, historique local et synchronisation du compte.

**English**
**What's new**
- **Local option removed**: the “Not now, stay local” button is gone. Signing in is now required to use the counter.
- **Started with Fortnite**: the sign-in screen also appears when the counter starts automatically with the game; the app returns to the notification area after signing in.
- **Forgot password**: a link on the sign-in screen sends a Supabase email; the return to the app lets you choose a new password without leaving ChestCounter.
- **Desktop shortcut**: created automatically on the first launch of the application.
- **Unchanged**: overlay, reports, local history and account sync.

**Español**
**Novedades**
- **Opción local eliminada**: se ha quitado el botón «Más tarde, seguir en local». Ahora es necesario iniciar sesión para usar el contador.
- **Inicio con Fortnite**: la pantalla de inicio de sesión también aparece cuando el contador se inicia automáticamente con el juego; la aplicación vuelve al área de notificación después de iniciar sesión.
- **Contraseña olvidada**: un enlace en la pantalla de inicio de sesión envía un correo de Supabase; al volver a la aplicación puedes elegir una nueva contraseña sin salir de ChestCounter.
- **Acceso directo en el escritorio**: se crea automáticamente en el primer inicio de la aplicación.
- **Sin cambios**: overlay, informes, historial local y sincronización de la cuenta.

---

**1.5.2 — Un seul overlay de stream**

**Français**
**Nouveautés**
- **Grande carte retirée** : l'onglet OBS / TikTok Live Studio ne propose plus que l'overlay de jeu compact. La carte complète n'est plus générée.
- **Anciens liens conservés** : un lien `/overlay` déjà configuré dans OBS ou TikTok affiche maintenant l'overlay compact, sans avoir à recréer la source.
- **Reste inchangé** : la carte violette en jeu, les rapports, la synchronisation du compte et les autres réglages continuent de fonctionner comme avant.

**English**
**What's new**
- **Full card removed**: the OBS / TikTok Live Studio section now only offers the compact game overlay. The full card is no longer generated.
- **Old links kept**: an existing `/overlay` source in OBS or TikTok now shows the compact overlay, with no need to recreate it.
- **Unchanged**: the purple in-game card, reports, account sync and the other settings keep working as before.

**Español**
**Novedades**
- **Tarjeta grande eliminada**: la sección OBS / TikTok Live Studio solo ofrece ahora el overlay compacto del juego. La tarjeta completa ya no se genera.
- **Enlaces antiguos conservados**: una fuente `/overlay` ya configurada en OBS o TikTok muestra ahora el overlay compacto, sin tener que recrearla.
- **Sin cambios**: la tarjeta violeta del juego, los informes, la sincronización de la cuenta y los demás ajustes siguen funcionando igual.

---

Windows peut afficher « Windows a protégé votre ordinateur » (installateur non signé) : **Informations complémentaires → Exécuter quand même**. Téléchargement : https://github.com/emilien-atr/ChestCounterFortnite/releases/latest
