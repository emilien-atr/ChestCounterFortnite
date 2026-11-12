<div align="center">

<img src="docs/icon.png" alt="ChestCounterFortnite" width="88">

# ChestCounterFortnite

**Tes coffres Fortnite, comptés automatiquement.**

Un overlay discret, un rapport clair et toutes tes données sur ton PC.

[![Télécharger](https://img.shields.io/badge/T%C3%A9l%C3%A9charger-la%20derni%C3%A8re%20version-E8B04B?style=for-the-badge)](https://github.com/emilien-atr/ChestCounterFortnite/releases/latest)
[![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%7C%2011-3A7BD5?style=flat-square)](#)
[![Licence MIT](https://img.shields.io/badge/licence-MIT-555?style=flat-square)](LICENSE)

<img src="docs/app-fr.png" alt="Interface ChestCounterFortnite" width="1080">

</div>

## En bref

- Compte les coffres à partir des **rediffusions Fortnite**, quelques secondes après chaque partie.
- Ne prend en compte que les parties **à partir du 1er novembre 2026**.
- Affiche un **HUD sur une ligne** ou la **carte violette illustrée**, avec le total, la dernière partie et les coffres cassés.
- Fournit un **lien de source navigateur** pour afficher le compteur dans OBS ou TikTok Live Studio.
- **Activité Discord** : si Discord desktop est ouvert, ton profil affiche le compteur avec le logo et un bouton GitHub.
- Garde l’historique, les sessions et les trésors dans un **rapport web** local.
- Français, anglais et espagnol.
- **Mises à jour automatiques** depuis GitHub.
- Aucun compte, aucune publicité, aucune donnée de jeu envoyée ; l'activité Discord est optionnelle et n'envoie que le total de coffres à Discord.

## Installation

1. Télécharge la [dernière version](https://github.com/emilien-atr/ChestCounterFortnite/releases/latest).
2. Active les rediffusions dans **Fortnite → Paramètres → Jeu → Rediffusions → Enregistrer les rediffusions de Battle Royale**, puis clique sur **Appliquer**. Sans replay, aucun coffre ne peut être compté.
3. Lance `ChestCounterFortnite.exe`. Windows peut afficher un avertissement car l’exécutable n’est pas signé : **Informations complémentaires → Exécuter quand même**.

## Mises à jour automatiques

À partir de la version **1.2**, chaque mise à jour du dépôt (`push` sur `main`) construit et publie une nouvelle release. Le compteur vérifie GitHub au démarrage puis régulièrement : il télécharge la nouvelle version, remplace son exe et redémarre tout seul.

Une version antérieure à la 1.2 (sans updater) doit être remplacée **une seule fois** par cette version. Ensuite, plus besoin de retélécharger quoi que ce soit.

## Période comptée

Seules les parties **commencées le 1er novembre 2026 ou après** sont comptées et affichées dans le compteur, l’overlay et le rapport. Les replays plus anciens restent dans `data.json` mais ne participent plus aux statistiques.

## Calcul des coffres

Le total additionne **tous les coffres trouvés dans les replays** : coffres classiques, rares, enterrés, coffres d’esprit, coffres des affiches et autres coffres. Les coffres **cassés au lieu d’être ouverts** sont inclus, et la ligne **« dont cassés »** indique leur part dans le total. Les caisses de munitions, caisses de nourriture et autres conteneurs restent suivis séparément dans le rapport.

## Interface

- **Accueil** : total, session, dernière partie, profil et actions rapides (rapport, overlay, verrouillage).
- **Réglages** : langue, overlay, lien OBS/TikTok Live Studio, dossier des replays, totaux de saison et comportement.
- **Aide** : rappels, raccourcis et dossier de données.

Le rapport s’ouvre dans le navigateur et se met à jour automatiquement pendant l’analyse.

## Raccourcis

| Raccourci | Action |
|---|---|
| **Ctrl+Maj+O** | Afficher ou masquer l’overlay, même en jeu. |
| **Ctrl+Maj+L** | Verrouiller ou déverrouiller l’overlay. Verrouillé, la souris le traverse ; déverrouillé, maintiens le clic gauche pour le déplacer et utilise le clic droit pour le masquer. |

## Overlay

<img src="docs/overlay-fr.png" alt="Carte violette et HUD sur une ligne" width="360">

- **HUD** : une seule ligne, dans l’esprit du HUD Fortnite. Idéal sous la minimap.
- **Carte violette** : la carte illustrée avec un grand coffre, le total en or, la dernière partie et la ligne « dont cassés ». Le même rendu est utilisé pour la source navigateur OBS/TikTok.

La taille se règle de **50 à 175 %** et l’opacité de **30 à 100 %** dans **Réglages → Overlay en jeu**. Pour l’afficher au-dessus du jeu, utilise le mode **Plein écran fenêtré** de Fortnite.

## OBS / TikTok Live Studio

Pour afficher le compteur dans un live :

1. Ouvre **Réglages → OBS / TikTok Live Studio** et clique sur **Copier le lien**.
2. Dans OBS, ajoute une source **Navigateur** et colle le lien. Dans TikTok Live Studio, utilise sa source navigateur si ta version en propose une.
3. Règle la taille de la source sur **1920 × 1080** (ou la taille de ton canvas) : le fond de la page est transparent.

Le lien est servi uniquement sur ce PC, à l’adresse `127.0.0.1` affichée dans les réglages. Le compteur doit rester ouvert pendant le live ; aucun réglage réseau, compte ou hébergement externe n’est nécessaire. Le port peut changer si le port préféré est déjà occupé : utilise toujours le lien affiché dans l’application.

## Données

Tout reste sur le PC, dans `%APPDATA%\ChestCounterFortnite` :

- `data.json` : historique ;
- `config.json` : réglages et position de l’overlay ;
- `report.html` : rapport web ;
- `matches.csv` : parties exportables ;
- `replay-archive/` : `replays.zip`, une archive ZIP unique contenant les replays déjà analysés déplacés depuis le dossier Fortnite (optionnel).

Le compteur ne modifie jamais Fortnite et ne lit pas sa mémoire. Aucune donnée n’est envoyée sur Internet, hors vérification des mises à jour GitHub et activité Discord optionnelle. Le lien de diffusion est servi uniquement en local sur `127.0.0.1`.

## Compiler

Prérequis : **.NET 10 SDK** et Windows 10/11.

```bat
dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

## Licence et crédits

[MIT](LICENSE) © 2026 **Emilien Autran**. Projet indépendant inspiré de [LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter) (MIT), avec [FortniteReplayReader](https://github.com/Shiqan/FortniteReplayReader), OozSharp, Unreal.Core, [DiscordRichPresence](https://github.com/Lachee/discord-rpc-csharp) (MIT) et la police Pirata One (SIL OFL).

Ce projet n’est ni affilié, ni approuvé, ni sponsorisé par Epic Games. Fortnite est une marque d’Epic Games, Inc.
