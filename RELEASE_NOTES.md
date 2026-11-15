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
- **Desktop shortcut**: created automatically on the first launch of the app.
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

Windows peut afficher « Windows a protégé votre ordinateur » (exe non signé) : **Informations complémentaires → Exécuter quand même**. Téléchargement : https://github.com/emilien-atr/ChestCounterFortnite/releases/latest
