# Compte et synchronisation Supabase

La connexion est **optionnelle** : sans compte, ChestCounterFortnite continue de fonctionner exactement comme avant, avec son historique local. Une fois connecté, l'historique et les statistiques sont associés au compte et récupérés sur les autres PC.

## 1. Créer le projet

1. Ouvre [supabase.com/dashboard](https://supabase.com/dashboard) et crée un projet.
2. Dans **SQL Editor**, ouvre et exécute [`supabase.sql`](supabase.sql). Il crée la table `public.chest_counter_state` et les règles RLS : chaque utilisateur ne peut lire et modifier que sa propre ligne. Le script peut être relancé sans risque et complète aussi une table créée par une version antérieure.
3. Dans **Project Settings → API**, note :
   - l'**URL du projet** (`https://xxxxx.supabase.co`) ;
   - la clé publique **anon** ou **publishable**.

Ne mets jamais la clé `service_role` dans l'application : elle contourne les règles RLS.

## 2. Connexion par e-mail

Dans **Authentication → Providers → Email**, laisse Email activé.

Si **Confirm email** est activé, après une inscription l'application affiche « Vérifie ta boîte mail ». Clique sur le lien reçu, puis reviens dans l'application et connecte-toi.

## 3. Connexion Google

1. Dans **Authentication → Providers → Google**, active le fournisseur.
2. Dans [Google Cloud Console](https://console.cloud.google.com/apis/credentials), crée un **ID client OAuth** de type *Application Web*.
3. Dans **URI de redirection autorisés**, ajoute exactement :

   ```
   https://<project-ref>.supabase.co/auth/v1/callback
   ```

   Remplace `<project-ref>` par la référence du projet Supabase visible dans son URL.
4. Copie le **Client ID** et le **Client Secret** dans la configuration Google de Supabase, puis enregistre.

## 4. Autoriser le retour vers l'application

Dans **Authentication → URL Configuration** :

- **Site URL** : `http://127.0.0.1:53682`
- **Redirect URLs** : ajoute exactement

  ```
  http://127.0.0.1:53682/auth/callback
  ```

C'est l'adresse du petit serveur local qui reçoit le retour de Google. Le port est fixe ; s'il est déjà occupé, ferme l'autre tentative et réessaie.

## 5. Configurer l'application

Au premier lancement, la page de connexion s'ouvre. Clique sur **Configurer Supabase**, colle l'URL du projet et la clé publique, puis **Enregistrer**. Les valeurs sont conservées dans :

```
%APPDATA%\ChestCounterFortnite\supabase.json
```

La clé publique peut être utilisée dans une application de bureau ; les droits réels sont contrôlés par les règles RLS. La session est enregistrée dans `supabase-session.json` dans le même dossier. Utilise **Déconnexion** dans **Réglages → Synchronisation** pour l'effacer.

## Fonctionnement de la synchronisation

- Dès la connexion (et à chaque lancement avec une session enregistrée), l'application récupère la ligne du compte, fusionne les parties par `SessionId`, puis renvoie l'ensemble.
- Une partie comptée sur deux PC n'apparaît qu'une fois ; la version d'analyse la plus récente est conservée.
- Les fichiers de replay ne sont **pas** envoyés : seule l'historique déjà calculé (`data.json`) est synchronisé.
- Les réglages d'affichage (overlay, langue, dossier des replays) restent propres à chaque PC. Les totaux manuels de saison (coffres classiques, rares et d'esprit) sont synchronisés avec l'historique.
- La synchronisation se fait automatiquement après une nouvelle partie et périodiquement. Le bouton **Synchroniser** des réglages la force immédiatement.
- En cas de perte de réseau, l'application continue en local et réessaie plus tard.

## Dépannage

| Message | À vérifier |
|---|---|
| `Supabase n'est pas configuré` | URL et clé publique dans la page de connexion. |
| `E-mail ou mot de passe incorrect` | Mot de passe, ou confirmation de l'adresse e-mail. |
| `Invalid login credentials` côté Supabase | Provider Email activé et compte confirmé. |
| La fenêtre Google reste ouverte | L'URL de redirection autorisée doit être exactement `http://127.0.0.1:53682/auth/callback`. |
| `Le port de connexion 53682 est déjà utilisé` | Ferme l'onglet ou l'instance qui utilise ce port, puis réessaie. |
| `new row violates row-level security policy` | Le SQL `supabase.sql` n'a pas été exécuté, ou la session n'est pas valide. |
