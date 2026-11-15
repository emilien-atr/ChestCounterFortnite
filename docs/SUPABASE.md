# Compte et synchronisation Supabase

La connexion est **nécessaire** pour utiliser ChestCounterFortnite. Une fois connecté, l'historique et les statistiques sont associés au compte et récupérés sur les autres PC ; les données restent aussi enregistrées localement.

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

## 3. Mot de passe oublié

Depuis l'écran de connexion, clique sur **Mot de passe oublié ?**, puis indique l'adresse du compte. Supabase envoie un lien de réinitialisation ; l'application reste ouverte et attend le retour sur le même callback local que Google.

1. Ouvre le lien reçu **sur le même PC**, dans le navigateur. Le lien revient vers `http://127.0.0.1:53682/auth/callback`.
2. L'application vérifie le code PKCE et affiche le formulaire **Nouveau mot de passe**.
3. Choisis un mot de passe d'au moins 6 caractères, confirme-le, puis clique sur **Mettre à jour le mot de passe**. La session est alors ouverte et la synchronisation reprend.

Garde ChestCounterFortnite ouvert pendant l'opération : le petit serveur local doit être disponible pour recevoir le lien. Si l'application est fermée, le lien ne peut pas aboutir ; recommence la demande. Aucune URL de redirection supplémentaire n'est nécessaire, `http://127.0.0.1:53682/auth/callback` suffit pour la connexion et pour la réinitialisation.

Le message affiché après l'envoi est volontairement générique : il ne révèle pas si une adresse possède un compte. Si l'e-mail n'arrive pas, vérifie les indésirables, le quota d'e-mails de Supabase et la configuration SMTP du projet.

## 4. Connexion Google

1. Dans **Authentication → Providers → Google**, active le fournisseur.
2. Dans [Google Cloud Console](https://console.cloud.google.com/apis/credentials), crée un **ID client OAuth** de type *Application Web*.
3. Dans **URI de redirection autorisés**, ajoute exactement :

   ```
   https://<project-ref>.supabase.co/auth/v1/callback
   ```

   Remplace `<project-ref>` par la référence du projet Supabase visible dans son URL.
4. Copie le **Client ID** et le **Client Secret** dans la configuration Google de Supabase, puis enregistre.

## 5. Autoriser le retour vers l'application

Dans **Authentication → URL Configuration** :

- **Site URL** : `http://127.0.0.1:53682`
- **Redirect URLs** : ajoute exactement

  ```
  http://127.0.0.1:53682/auth/callback
  ```

C'est l'adresse du petit serveur local qui reçoit le retour de Google et le lien de réinitialisation du mot de passe. Le port est fixe ; s'il est déjà occupé, ferme l'autre tentative et réessaie.

## 6. Configuration de l'application

L'URL du projet et la clé publique sont intégrées à l'application : les utilisateurs n'ont aucune configuration Supabase à saisir, et l'interface ne les affiche nulle part. Le fichier local `supabase.json` ne contient que l'identifiant de l'appareil ; la session est enregistrée dans `supabase-session.json`. Utilise **Déconnexion** dans **Réglages → Synchronisation** pour effacer la session.

Une clé Supabase **publishable** est destinée à être embarquée dans un client : elle n'accorde aucun droit à elle seule, les règles RLS limitent chaque compte à ses propres données. La clé **service_role** ne doit jamais être mise dans l'application.

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
| `Connexion impossible` | Vérifie la connexion Internet et le projet Supabase. |
| `E-mail ou mot de passe incorrect` | Mot de passe, ou confirmation de l'adresse e-mail. |
| `Invalid login credentials` côté Supabase | Provider Email activé et compte confirmé. |
| La fenêtre Google reste ouverte | L'URL de redirection autorisée doit être exactement `http://127.0.0.1:53682/auth/callback`. |
| Aucun e-mail de réinitialisation | Vérifie les indésirables, le quota d'e-mails Supabase et la configuration SMTP. Le message affiché ne confirme pas l'existence du compte. |
| Le lien de réinitialisation ne revient pas dans l'application | Ouvre-le sur le même PC et garde ChestCounterFortnite ouvert ; le callback local doit pouvoir écouter sur le port 53682. |
| `Le lien de réinitialisation n'est plus valide` | Le lien a expiré ou a déjà été utilisé. Recommence la demande depuis **Mot de passe oublié ?**. |
| `Le port de connexion 53682 est déjà utilisé` | Ferme l'onglet ou l'instance qui utilise ce port, puis réessaie. |
| `new row violates row-level security policy` | Le SQL `supabase.sql` n'a pas été exécuté, ou la session n'est pas valide. |
