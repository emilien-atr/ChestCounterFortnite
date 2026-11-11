<div align="center">

<img src="rapport-final.png" alt="Rapport ChestCounterFortnite" width="860">

# ChestCounterFortnite

**Tes coffres Fortnite, comptés automatiquement.**
Un overlay discret en jeu, un rapport clair, et toutes tes données sur ton PC.

[![Télécharger la 1.1.0](https://img.shields.io/badge/T%C3%A9l%C3%A9charger-v1.1.0-E8B04B?style=for-the-badge)](https://github.com/emilien-atr/ChestCounterFortnite/releases/latest)
[![Windows 10 / 11](https://img.shields.io/badge/Windows-10%20%7C%2011-3A7BD5?style=flat-square)](#)
[![Licence MIT](https://img.shields.io/badge/licence-MIT-555?style=flat-square)](LICENSE)

</div>

---

## Ce que fait le compteur

- **Compte tes coffres** à partir des rediffusions enregistrées par Fortnite, quelques secondes après chaque partie.
- **Affiche un overlay** par-dessus le jeu : total, coffres de la session, coffres cassés.
- **Garde ton historique** : dernières parties, répartition par type de coffre, trésors trouvés.
- **Reste simple et local** : aucun compte, aucune donnée envoyée, aucune publicité.
- **Parle français, anglais et espagnol.**

<p align="center">
  <img src="overlay-live.png" alt="Overlay en jeu" height="126">
</p>

## Installation

1. **[Télécharge ChestCounterFortnite.exe](https://github.com/emilien-atr/ChestCounterFortnite/releases/latest)**.
2. Active les rediffusions dans Fortnite (voir juste en dessous).
3. Lance l’exécutable. Au premier démarrage, choisis ta langue et vérifie le dossier des replays.

> Windows peut afficher **« Windows a protégé votre ordinateur »** : clique sur **Informations complémentaires → Exécuter quand même**. L’exécutable n’est pas signé.

## Rien n’est compté ?

Fortnite doit enregistrer la partie pour que le compteur puisse la lire.

**Dans Fortnite :** `Paramètres → Jeu → Rediffusions` → active **Enregistrer les rediffusions de Battle Royale**, puis clique sur **Appliquer**.

La version **1.1.0** vérifie ce réglage dans les logs de Fortnite. Si l’enregistrement est refusé, elle affiche un avertissement clair dans la fenêtre et dans le rapport, avec la date du dernier replay. Elle ne peut pas compter une partie qui n’a pas été enregistrée.

Le dossier des replays est normalement :

```text
%LOCALAPPDATA%\FortniteGame\Saved\Demos
```

## Où sont mes données ?

Tout reste sur ce PC, dans :

```text
%APPDATA%\ChestCounterFortnite\
```

`data.json` contient l’historique, `config.json` tes réglages et `report.html` le rapport généré. Le compteur ne modifie jamais Fortnite et ne lit pas sa mémoire.

## L’overlay

Deux styles sont disponibles dans les réglages :

- **Classique** — une carte compacte avec le logo doré.
- **HUD** — une ligne discrète, dans l’esprit de l’interface Fortnite.

`Ctrl+Maj+O` affiche ou masque l’overlay. `Ctrl+Maj+L` le verrouille pour qu’il ne bouge plus en jeu. En jeu, utilise le mode **Plein écran fenêtré**.

## Compiler

Prérequis : **.NET 10 SDK** et Windows 10/11.

```bat
git clone https://github.com/emilien-atr/ChestCounterFortnite.git
cd ChestCounterFortnite
dotnet publish src/ChestCounter/ChestCounter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

L’exécutable est créé dans `dist\ChestCounterFortnite.exe`.

## Crédits

Projet indépendant, inspiré de **[LED-N/Fortnite-ChestCounter](https://github.com/LED-N/Fortnite-ChestCounter)** (MIT). Merci à LED-N et aux auteurs des bibliothèques utilisées :

- [FortniteReplayReader](https://github.com/Shiqan/FortniteReplayReader)
- [OozSharp](https://github.com/KnowingNothing/OozSharp)
- [Unreal.Core](https://github.com/Shiqan/FortniteReplayReader)
- Police **Pirata One** (SIL Open Font License)

## Licence

[MIT](LICENSE) — utilise-le, modifies-le, partage-le.
