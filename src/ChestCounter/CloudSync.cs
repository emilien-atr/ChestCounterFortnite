using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChestCounter;

/// <summary>
/// Optional Supabase account: email/password sign-in, Google OAuth with a loopback callback, and a
/// per-user copy of the history. The local application keeps working when Supabase is not configured.
/// </summary>
internal sealed class CloudService
{
    public const string CallbackUrl = "http://127.0.0.1:53682/auth/callback";
    private const int CallbackPort = 53682;
    private const string Table = "chest_counter_state";

    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Store _store;
    private readonly TrackerData _data;
    private readonly Config _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private CloudConfig _config;
    private CloudSession? _session;
    private CancellationTokenSource? _googleCancellation;
    private CloudSession? _recoverySession;
    private CancellationTokenSource? _recoveryCancellation;
    private string _recoveryStage = "idle";
    private string _recoveryEmail = "";
    private string _recoveryError = "";
    private string _sync = "off";
    private string _error = "";
    private DateTime? _lastSyncUtc;
    private bool _busy;

    public event Action? Changed;
    /// <summary>Raised on the calling thread when remote data was merged into the shared history.</summary>
    public event Action? DataMerged;

    public CloudService(Store store, TrackerData data, Config settings)
    {
        _store = store;
        _data = data;
        _settings = settings;
        _config = LoadConfig();
        _session = LoadSession();
        _sync = _config.IsConfigured ? (_session != null ? "idle" : "off") : "off";
        if (_session != null)
        {
            _sync = "idle";
        }
        EnsureDeviceId();
    }

    public bool Configured => _config.IsConfigured;
    public bool SignedIn => _session != null;
    public bool Busy => _busy;
    public string? Email => _session?.Email;
    public string? Name => _session?.Name;
    public string? Provider => _session?.Provider;
    public string? UserId => _session?.UserId;
    public string Sync => _sync;
    public string Error => _error;
    public DateTime? LastSyncUtc => _lastSyncUtc;
    public string DeviceId => _config.DeviceId;
    public string RecoveryStage => _recoveryStage;
    public string RecoveryEmail => _recoveryEmail;
    public string RecoveryError => _recoveryError;
    public bool RecoveryActive => _recoveryStage is "sent" or "ready";

    public CloudSnapshot Snapshot() => new(
        Configured,
        SignedIn,
        Email ?? "",
        Name ?? "",
        Provider ?? "",
        _sync,
        _lastSyncUtc,
        _error,
        _busy,
        CallbackUrl,
        _recoveryStage,
        _recoveryEmail,
        _recoveryError);

    /// <summary>Checks a saved session when the window opens, refreshing it if it has expired.</summary>
    public async Task InitializeAsync()
    {
        if (_session == null)
        {
            return;
        }
        await EnsureFreshSessionAsync();
        Changed?.Invoke();
    }

    public async Task<CloudResult> SignInAsync(string email, string password)
    {
        if (!Configured)
        {
            return CloudResult.Fail("Supabase n'est pas configuré.");
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return CloudResult.Fail("Indique ton adresse e-mail et ton mot de passe.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/auth/v1/token?grant_type=password"));
            AddApiHeaders(request);
            request.Content = JsonBody(new { email = email.Trim(), password });
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return CloudResult.Fail(AuthError(body, response.StatusCode));
            }
            using var document = JsonDocument.Parse(body);
            if (!ApplySession(document.RootElement, "password"))
            {
                return CloudResult.Fail("Supabase n'a pas renvoyé de session.");
            }
            _error = "";
            _sync = "idle";
            Changed?.Invoke();
            return CloudResult.Ok("Connecté.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CloudResult.Fail(NetworkError(ex));
        }
    }

    public async Task<CloudResult> SignUpAsync(string email, string password)
    {
        if (!Configured)
        {
            return CloudResult.Fail("Supabase n'est pas configuré.");
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            return CloudResult.Fail("Indique une adresse e-mail et un mot de passe.");
        }
        if (password.Length < 6)
        {
            return CloudResult.Fail("Le mot de passe doit contenir au moins 6 caractères.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/auth/v1/signup"));
            AddApiHeaders(request);
            request.Content = JsonBody(new { email = email.Trim(), password });
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return CloudResult.Fail(AuthError(body, response.StatusCode));
            }

            using var document = JsonDocument.Parse(body);
            if (ApplySession(document.RootElement, "password"))
            {
                _error = "";
                _sync = "idle";
                Changed?.Invoke();
                return CloudResult.Ok("Compte créé et connecté.");
            }

            // Email confirmation is enabled on many Supabase projects: there is no session yet.
            return CloudResult.Ok("Compte créé. Vérifie ta boîte mail pour confirmer l'adresse, puis connecte-toi.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CloudResult.Fail(NetworkError(ex));
        }
    }

    /// <summary>
    /// Google sign-in with PKCE. The browser returns to a short-lived loopback listener, so no custom
    /// URL scheme and no client secret is needed.
    /// </summary>
    public async Task<CloudResult> SignInWithGoogleAsync()
    {
        if (!Configured)
        {
            return CloudResult.Fail("Supabase n'est pas configuré.");
        }
        if (_busy)
        {
            return CloudResult.Fail("Une connexion est déjà en cours.");
        }

        var providerCheck = await CheckGoogleProviderAsync();
        if (!providerCheck.Succeeded)
        {
            return providerCheck;
        }

        _busy = true;
        Changed?.Invoke();
        TcpListener? listener = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _googleCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var cancellationToken = _googleCancellation.Token;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.Start();
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorize = ApiUrl("/auth/v1/authorize")
                + "?provider=google"
                + "&scopes=" + Uri.EscapeDataString("email profile")
                + "&redirect_to=" + Uri.EscapeDataString(CallbackUrl)
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=s256";

            Process.Start(new ProcessStartInfo(authorize) { UseShellExecute = true })?.Dispose();

            var code = await WaitForCallbackAsync(listener, cancellationToken);
            if (string.IsNullOrEmpty(code))
            {
                return CloudResult.Fail("Connexion Google annulée.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/auth/v1/token?grant_type=pkce"));
            AddApiHeaders(request);
            request.Content = JsonBody(new { auth_code = code, code_verifier = verifier });
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return CloudResult.Fail(AuthError(body, response.StatusCode));
            }
            using var document = JsonDocument.Parse(body);
            if (!ApplySession(document.RootElement, "google"))
            {
                return CloudResult.Fail("Google n'a pas renvoyé de session.");
            }
            _error = "";
            _sync = "idle";
            Changed?.Invoke();
            return CloudResult.Ok("Connecté avec Google.");
        }
        catch (OperationCanceledException)
        {
            return CloudResult.Fail("Connexion Google annulée.");
        }
        catch (SocketException)
        {
            return CloudResult.Fail("Le port de connexion 53682 est déjà utilisé. Ferme l'autre tentative puis réessaie.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CloudResult.Fail(NetworkError(ex));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Connexion Google", ex);
            return CloudResult.Fail("Connexion Google impossible : " + ex.Message);
        }
        finally
        {
            _googleCancellation?.Dispose();
            _googleCancellation = null;
            try { listener?.Stop(); } catch { /* already stopped */ }
            _busy = false;
            Changed?.Invoke();
        }
    }

    public void CancelGoogle()
    {
        try { _googleCancellation?.Cancel(); } catch { /* already closed */ }
    }

    /// <summary>
    /// Sends a Supabase recovery email and listens for its PKCE callback on the same loopback port as
    /// Google sign-in. The method returns once the email is sent; the callback is then handled in the
    /// background so the login screen can show a waiting state.
    /// </summary>
    public async Task<CloudResult> RequestPasswordResetAsync(string email)
    {
        if (!Configured)
        {
            return CloudResult.Fail("Supabase n'est pas configuré.");
        }
        if (_busy || RecoveryActive)
        {
            return CloudResult.Fail("Une demande de réinitialisation est déjà en cours.");
        }
        if (string.IsNullOrWhiteSpace(email))
        {
            return CloudResult.Fail("Indique l'adresse e-mail de ton compte.");
        }

        _busy = true;
        _recoveryError = "";
        Changed?.Invoke();
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.Start();
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var recoverUrl = ApiUrl("/auth/v1/recover?redirect_to=" + Uri.EscapeDataString(CallbackUrl));

            using var request = new HttpRequestMessage(HttpMethod.Post, recoverUrl);
            AddApiHeaders(request);
            request.Content = JsonBody(new
            {
                email = email.Trim(),
                code_challenge = challenge,
                code_challenge_method = "s256",
            });
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return CloudResult.Fail(AuthError(body, response.StatusCode));
            }

            _recoveryEmail = email.Trim();
            _recoveryStage = "sent";
            _recoveryError = "";
            _recoverySession = null;
            _recoveryCancellation?.Cancel();
            _recoveryCancellation?.Dispose();
            _recoveryCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var ownedListener = listener;
            listener = null;
            _ = WaitForRecoveryAsync(ownedListener, verifier, _recoveryCancellation.Token);
            return CloudResult.Ok("Si un compte existe avec cette adresse, un lien de réinitialisation vient d'être envoyé. Garde ChestCounter ouvert puis ouvre le lien.");
        }
        catch (SocketException)
        {
            return CloudResult.Fail("Le port de connexion 53682 est déjà utilisé. Ferme l'autre tentative puis réessaie.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CloudResult.Fail(NetworkError(ex));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Réinitialisation du mot de passe", ex);
            return CloudResult.Fail("Envoi impossible : " + ex.Message);
        }
        finally
        {
            try { listener?.Stop(); } catch { /* already stopped */ }
            _busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Updates the password with the temporary session returned by the recovery callback.</summary>
    public async Task<CloudResult> CompletePasswordResetAsync(string password)
    {
        if (_recoverySession == null || _recoveryStage != "ready")
        {
            return CloudResult.Fail("Le lien de réinitialisation n'est plus valide. Recommence la demande.");
        }
        if ((password ?? "").Length < 6)
        {
            return CloudResult.Fail("Le mot de passe doit contenir au moins 6 caractères.");
        }
        if (_busy)
        {
            return CloudResult.Fail("Une opération est déjà en cours.");
        }

        var recoverySession = _recoverySession!;
        _busy = true;
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, ApiUrl("/auth/v1/user"));
            AddApiHeaders(request, recoverySession.AccessToken);
            request.Content = JsonBody(new { password });
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var error = AuthError(body, response.StatusCode);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _recoverySession = null;
                    _recoveryStage = "idle";
                    _recoveryError = error;
                }
                return CloudResult.Fail(error);
            }

            if (_recoveryStage != "ready" || _recoverySession != recoverySession)
            {
                return CloudResult.Ok("Mot de passe mis à jour. Reconnecte-toi avec ton nouveau mot de passe.");
            }

            recoverySession.Provider = "password";
            _session = recoverySession;
            _recoverySession = null;
            _recoveryStage = "idle";
            _recoveryEmail = "";
            _recoveryError = "";
            _error = "";
            _sync = "idle";
            SaveSession();
            return CloudResult.Ok("Mot de passe mis à jour. Tu es connecté.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CloudResult.Fail(NetworkError(ex));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Mise à jour du mot de passe", ex);
            return CloudResult.Fail("Mise à jour impossible : " + ex.Message);
        }
        finally
        {
            _busy = false;
            Changed?.Invoke();
        }
    }

    public void CancelRecovery()
    {
        try { _recoveryCancellation?.Cancel(); } catch { /* already closed */ }
        _recoverySession = null;
        _recoveryStage = "idle";
        _recoveryEmail = "";
        _recoveryError = "";
        Changed?.Invoke();
    }

    private async Task WaitForRecoveryAsync(TcpListener listener, string verifier, CancellationToken cancellationToken)
    {
        try
        {
            var code = await WaitForCallbackAsync(listener, cancellationToken,
                "Lien de réinitialisation vérifié. Reviens dans ChestCounterFortnite pour choisir un nouveau mot de passe.");
            if (string.IsNullOrEmpty(code))
            {
                if (_recoveryStage == "sent")
                {
                    _recoveryStage = "idle";
                    _recoveryError = "Le lien n'a pas pu être validé. Recommence la demande.";
                }
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/auth/v1/token?grant_type=pkce"));
            AddApiHeaders(request);
            request.Content = JsonBody(new { auth_code = code, code_verifier = verifier });
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _recoveryStage = "idle";
                _recoveryError = AuthError(body, response.StatusCode);
                return;
            }

            using var document = JsonDocument.Parse(body);
            var session = ReadSession(document.RootElement, "recovery");
            if (session == null)
            {
                _recoveryStage = "idle";
                _recoveryError = "Supabase n'a pas renvoyé de session de réinitialisation.";
                return;
            }

            if (_recoveryStage != "sent")
            {
                return;
            }
            _recoverySession = session;
            _recoveryStage = "ready";
            _recoveryError = "";
        }
        catch (OperationCanceledException)
        {
            if (_recoveryStage == "sent")
            {
                _recoveryStage = "idle";
                _recoveryError = "Le lien a expiré. Recommence la demande.";
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Retour de réinitialisation", ex);
            if (_recoveryStage == "sent")
            {
                _recoveryStage = "idle";
                _recoveryError = "Le retour depuis l'e-mail a échoué : " + ex.Message;
            }
        }
        finally
        {
            try { listener.Stop(); } catch { /* already stopped */ }
            var cancellation = _recoveryCancellation;
            _recoveryCancellation = null;
            cancellation?.Dispose();
            Changed?.Invoke();
        }
    }

    public Task SignOutAsync()
    {
        _session = null;
        _error = "";
        _sync = Configured ? "off" : "off";
        _lastSyncUtc = null;
        TryDelete(SessionPath);
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public CloudResult Configure(string url, string anonKey)
    {
        var normalized = NormalizeUrl(url);
        if (normalized.Length == 0)
        {
            return CloudResult.Fail("L'URL Supabase est invalide.");
        }
        var key = (anonKey ?? "").Trim();
        if (key.Length < 20)
        {
            return CloudResult.Fail("La clé publique (anon ou publishable) est invalide.");
        }

        var previousUrl = _config.Url;
        _config.Url = normalized;
        _config.AnonKey = key;
        _config.IsConfigured = true;
        _error = "";
        _sync = _session != null && string.Equals(previousUrl, normalized, StringComparison.OrdinalIgnoreCase) ? "idle" : "off";
        if (!string.Equals(previousUrl, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // A different project cannot reuse the previous project's session.
            _session = null;
            TryDelete(SessionPath);
        }
        SaveConfig();
        Changed?.Invoke();
        return CloudResult.Ok("Configuration Supabase enregistrée. Tu peux te connecter.");
    }

    /// <summary>Pulls the account's history, merges it with the local one, then uploads the union.</summary>
    public async Task SyncAsync()
    {
        if (!Configured || _session == null || _busy)
        {
            return;
        }
        if (!await _syncGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            if (!await EnsureFreshSessionAsync())
            {
                return;
            }
            _sync = "syncing";
            _error = "";
            Changed?.Invoke();

            using var get = new HttpRequestMessage(HttpMethod.Get,
                ApiUrl($"/rest/v1/{Table}?user_id=eq.{Uri.EscapeDataString(_session!.UserId)}&select=data,updated_at"));
            AddApiHeaders(get, _session.AccessToken);
            using var response = await _http.SendAsync(get);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await SignOutAsync();
                _error = "La session a expiré. Reconnecte-toi.";
                Changed?.Invoke();
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new CloudRequestException(ReadError(body, response.StatusCode));
            }

            var merged = false;
            ManualChestTotals? remoteManual = null;
            using (var document = JsonDocument.Parse(body))
            {
                if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
                {
                    var row = document.RootElement[0];
                    if (row.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
                    {
                        var remote = dataElement.Deserialize<TrackerData>(ApiJson);
                        if (remote != null)
                        {
                            remoteManual = remote.ManualChests;
                            merged = MergeRemote(_data, remote) || merged;
                        }
                    }
                }
            }

            // Manual season totals are offsets on top of the local matches. A fresh PC (all zero) adopts the
            // account's values; a PC where the player already entered values keeps them and publishes them.
            var localManual = ManualFromConfig(_settings);
            if (remoteManual != null && !HasManualChests(localManual) && HasManualChests(remoteManual))
            {
                ApplyManualChests(_settings, remoteManual);
                try { _store.SaveConfig(_settings); } catch (Exception ex) { ErrorLog.Write("Configuration des totaux synchronisés", ex); }
                localManual = ManualFromConfig(_settings);
                merged = true;
            }
            if (HasManualChests(localManual))
            {
                if (!SameManualChests(_data.ManualChests, localManual))
                {
                    _data.ManualChests = localManual;
                    merged = true;
                }
            }
            else if (_data.ManualChests != null)
            {
                _data.ManualChests = null;
                merged = true;
            }

            if (merged)
            {
                DataMerged?.Invoke();
            }

            using var put = new HttpRequestMessage(HttpMethod.Post,
                ApiUrl($"/rest/v1/{Table}?on_conflict=user_id"));
            AddApiHeaders(put, _session.AccessToken);
            put.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
            put.Content = JsonBody(new
            {
                user_id = _session.UserId,
                data = _data,
                updated_at = DateTime.UtcNow.ToString("o"),
                device_id = DeviceId,
            });
            using var putResponse = await _http.SendAsync(put);
            var putBody = await putResponse.Content.ReadAsStringAsync();
            if (!putResponse.IsSuccessStatusCode)
            {
                throw new CloudRequestException(ReadError(putBody, putResponse.StatusCode));
            }

            _lastSyncUtc = DateTime.UtcNow;
            _sync = "synced";
            _error = "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _sync = "offline";
            _error = NetworkError(ex);
        }
        catch (CloudRequestException ex)
        {
            _sync = "error";
            _error = IsMissingTable(ex.Message)
                ? L.T("La table Supabase n'existe pas encore : exécute docs/supabase.sql dans le SQL Editor du projet.",
                    "The Supabase table does not exist yet: run docs/supabase.sql in the project's SQL Editor.",
                    "La tabla de Supabase aún no existe: ejecuta docs/supabase.sql en el SQL Editor del proyecto.")
                : ex.Message;
        }
        catch (JsonException)
        {
            _sync = "error";
            _error = "Réponse Supabase illisible.";
        }
        catch (Exception ex)
        {
            _sync = "error";
            _error = ex.Message;
            ErrorLog.Write("Synchronisation Supabase", ex);
        }
        finally
        {
            _busy = false;
            _syncGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Remote and local matches are keyed by SessionId: a replay counted on two PCs appears once, and the
    /// most recent analysis wins. Processed replay keys are unioned to avoid analyzing the same file twice.
    /// </summary>
    public static bool MergeRemote(TrackerData local, TrackerData remote)
    {
        var changed = false;
        local.Matches ??= new List<MatchRecord>();
        local.ProcessedFiles ??= new Dictionary<string, string>();
        remote.Matches ??= new List<MatchRecord>();
        remote.ProcessedFiles ??= new Dictionary<string, string>();

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < local.Matches.Count; i++)
        {
            var id = local.Matches[i].SessionId;
            if (!string.IsNullOrEmpty(id))
            {
                indexes.TryAdd(id, i);
            }
        }

        foreach (var incoming in remote.Matches)
        {
            if (string.IsNullOrEmpty(incoming.SessionId))
            {
                continue;
            }
            if (!indexes.TryGetValue(incoming.SessionId, out var index))
            {
                indexes[incoming.SessionId] = local.Matches.Count;
                local.Matches.Add(incoming);
                changed = true;
                continue;
            }

            var current = local.Matches[index];
            if (incoming.AnalyzerVersion > current.AnalyzerVersion
                || (incoming.AnalyzerVersion == current.AnalyzerVersion && incoming.AnalyzedAtUtc > current.AnalyzedAtUtc))
            {
                local.Matches[index] = incoming;
                changed = true;
            }
        }

        foreach (var pair in remote.ProcessedFiles)
        {
            if (!local.ProcessedFiles.ContainsKey(pair.Key))
            {
                local.ProcessedFiles[pair.Key] = pair.Value;
                changed = true;
            }
        }

        if (remote.Version > local.Version)
        {
            local.Version = remote.Version;
            changed = true;
        }
        if (remote.AnalyzerVersion > local.AnalyzerVersion)
        {
            local.AnalyzerVersion = remote.AnalyzerVersion;
            changed = true;
        }

        return changed;
    }

    private const int MaxManualChests = 1_000_000;

    private static ManualChestTotals ManualFromConfig(Config config) => new()
    {
        Classic = ClampManualChests(config.HeadStartChests),
        Rare = ClampManualChests(config.HeadStartRareChests),
        Sprite = ClampManualChests(config.HeadStartSpriteChests),
    };

    private static bool HasManualChests(ManualChestTotals? manual) =>
        manual != null && (manual.Classic > 0 || manual.Rare > 0 || manual.Sprite > 0);

    private static bool SameManualChests(ManualChestTotals? left, ManualChestTotals? right)
    {
        var leftHasValues = HasManualChests(left);
        var rightHasValues = HasManualChests(right);
        if (!leftHasValues || !rightHasValues)
        {
            return leftHasValues == rightHasValues;
        }
        return ClampManualChests(left!.Classic) == ClampManualChests(right!.Classic)
            && ClampManualChests(left.Rare) == ClampManualChests(right.Rare)
            && ClampManualChests(left.Sprite) == ClampManualChests(right.Sprite);
    }

    private static void ApplyManualChests(Config config, ManualChestTotals manual)
    {
        config.HeadStartChests = ClampManualChests(manual.Classic);
        config.HeadStartRareChests = ClampManualChests(manual.Rare);
        config.HeadStartSpriteChests = ClampManualChests(manual.Sprite);
    }

    private static int ClampManualChests(int value) => Math.Clamp(value, 0, MaxManualChests);

    private async Task<CloudResult> CheckGoogleProviderAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl("/auth/v1/settings"));
            AddApiHeaders(request);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                // Let the normal authorization flow report a more precise error.
                return CloudResult.Ok("");
            }
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("external", out var external)
                && external.TryGetProperty("google", out var google)
                && google.ValueKind is JsonValueKind.False)
            {
                return CloudResult.Fail(L.T("Google n'est pas activé sur ce projet Supabase. Active le provider Google dans Authentication → Providers.",
                    "Google is not enabled on this Supabase project. Enable the Google provider in Authentication → Providers.",
                    "Google no está activado en este proyecto de Supabase. Activa el proveedor Google en Authentication → Providers."));
            }
            return CloudResult.Ok("");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Configuration check is best effort; the actual OAuth flow stays authoritative.
            return CloudResult.Ok("");
        }
    }

    private static bool IsMissingTable(string message) =>
        message.Contains("Could not find the table", StringComparison.OrdinalIgnoreCase)
        || message.Contains("PGRST205", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> EnsureFreshSessionAsync()
    {
        if (_session == null)
        {
            return false;
        }
        if (_session.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(2))
        {
            return true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/auth/v1/token?grant_type=refresh_token"));
            AddApiHeaders(request);
            request.Content = JsonBody(new { refresh_token = _session.RefreshToken });
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                return ApplySession(document.RootElement, _session.Provider, _session);
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                await SignOutAsync();
                _error = "La session a expiré. Reconnecte-toi.";
                Changed?.Invoke();
            }
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _error = NetworkError(ex);
            return _session != null;
        }
    }

    private async Task<string?> WaitForCallbackAsync(TcpListener listener, CancellationToken cancellationToken,
        string successMessage = "Connexion réussie. Tu peux fermer cette fenêtre et revenir à ChestCounterFortnite.")
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var firstLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                continue;
            }

            var parts = firstLine.Split(' ');
            if (parts.Length < 2 || !parts[1].StartsWith("/auth/callback", StringComparison.Ordinal))
            {
                await WriteBrowserResponseAsync(stream, 404, "Page introuvable.");
                continue;
            }

            var target = new Uri("http://127.0.0.1" + parts[1]);
            var query = ParseQuery(target.Query);
            if (query.TryGetValue("error_description", out var description))
            {
                await WriteBrowserResponseAsync(stream, 400, "Connexion refusée : " + WebUtility.HtmlEncode(description));
                return null;
            }
            if (query.TryGetValue("error", out var error))
            {
                await WriteBrowserResponseAsync(stream, 400, "Connexion refusée : " + WebUtility.HtmlEncode(error));
                return null;
            }
            if (query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code))
            {
                await WriteBrowserResponseAsync(stream, 200, successMessage);
                return code;
            }

            await WriteBrowserResponseAsync(stream, 400, "Réponse Supabase incomplète.");
            return null;
        }
        return null;
    }

    private static async Task WriteBrowserResponseAsync(NetworkStream stream, int status, string message)
    {
        var html = "<!doctype html><html lang=fr><meta charset=utf-8><title>ChestCounterFortnite</title>"
            + "<body style=\"margin:0;min-height:100vh;display:grid;place-items:center;background:#0b0716;color:#f2eef8;font-family:Segoe UI,sans-serif\">"
            + "<div style=\"max-width:520px;padding:32px;text-align:center\"><h1 style=\"color:#f6b73c;font-size:24px\">ChestCounterFortnite</h1>"
            + "<p style=\"line-height:1.6;color:#c9c0da\">" + WebUtility.HtmlEncode(message) + "</p></div></body></html>";
        var reason = status == 200 ? "OK" : "Bad Request";
        var bytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            result[key] = value;
        }
        return result;
    }

    private static CloudSession? ReadSession(JsonElement root, string provider, CloudSession? fallback = null)
    {
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var refresh = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() ?? "" : "";
        if (refresh.Length == 0 && fallback != null)
        {
            refresh = fallback.RefreshToken;
        }
        if (refresh.Length == 0)
        {
            return null;
        }
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds)
            ? Math.Max(60, seconds)
            : 3600;

        string userId = "";
        string email = "";
        string name = "";
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            userId = user.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            email = user.TryGetProperty("email", out var mail) ? mail.GetString() ?? "" : "";
            if (user.TryGetProperty("user_metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "full_name", "name", "user_name", "preferred_username" })
                {
                    if (metadata.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        name = value.GetString() ?? "";
                        if (name.Length > 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        if (fallback != null)
        {
            // A refresh response without a user object still belongs to the current session.
            userId = userId.Length > 0 ? userId : fallback.UserId;
            email = email.Length > 0 ? email : fallback.Email;
            name = name.Length > 0 ? name : fallback.Name;
        }
        if (userId.Length == 0)
        {
            // The access token is the source of truth; without a user id the REST row cannot be scoped.
            return null;
        }

        return new CloudSession
        {
            AccessToken = access.GetString() ?? "",
            RefreshToken = refresh,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn - 30),
            UserId = userId,
            Email = email,
            Name = name,
            Provider = provider,
        };
    }

    private bool ApplySession(JsonElement root, string provider, CloudSession? fallback = null)
    {
        var session = ReadSession(root, provider, fallback);
        if (session == null)
        {
            return false;
        }
        _session = session;
        SaveSession();
        return true;
    }

    private HttpRequestMessage AddApiHeaders(HttpRequestMessage request, string? accessToken = null)
    {
        request.Headers.TryAddWithoutValidation("apikey", _config.AnonKey);
        if (!string.IsNullOrEmpty(accessToken))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        }
        return request;
    }

    private static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private string ApiUrl(string path) => _config.Url.TrimEnd('/') + path;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeUrl(string raw)
    {
        var value = (raw ?? "").Trim().TrimEnd('/');
        if (value.Length == 0)
        {
            return "";
        }
        if (value.EndsWith("/rest/v1", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^8];
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            return "";
        }
        return value.TrimEnd('/');
    }

    private static string ReadError(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            foreach (var key in new[] { "error_description", "msg", "message", "error", "error_code" })
            {
                if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
        }
        catch
        {
            // fall through
        }
        return $"Erreur Supabase ({(int)status}).";
    }

    private static string AuthError(string body, HttpStatusCode status)
    {
        var message = ReadError(body, status);
        if (status == HttpStatusCode.BadRequest && message.Contains("Invalid login", StringComparison.OrdinalIgnoreCase))
        {
            return "E-mail ou mot de passe incorrect.";
        }
        if (status == HttpStatusCode.UnprocessableEntity && message.Contains("already registered", StringComparison.OrdinalIgnoreCase))
        {
            return "Un compte existe déjà avec cette adresse.";
        }
        return message;
    }

    private static string NetworkError(Exception ex) =>
        ex is TaskCanceledException
            ? "Supabase ne répond pas. Vérifie ta connexion internet."
            : "Connexion à Supabase impossible. Vérifie ta connexion internet.";

    private CloudConfig LoadConfig()
    {
        var config = ReadJson<CloudConfig>(ConfigPath) ?? new CloudConfig();
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            config.Url = Environment.GetEnvironmentVariable("CHESTCOUNTER_SUPABASE_URL") ?? "";
        }
        if (string.IsNullOrWhiteSpace(config.AnonKey))
        {
            config.AnonKey = Environment.GetEnvironmentVariable("CHESTCOUNTER_SUPABASE_ANON_KEY") ?? "";
        }
        config.Url = NormalizeUrl(config.Url);
        config.AnonKey = config.AnonKey.Trim();
        config.IsConfigured = config.Url.Length > 0 && config.AnonKey.Length >= 20;
        return config;
    }

    private CloudSession? LoadSession()
    {
        try
        {
            var session = ReadJson<CloudSession>(SessionPath);
            if (session == null || string.IsNullOrWhiteSpace(session.AccessToken)
                || string.IsNullOrWhiteSpace(session.RefreshToken) || string.IsNullOrWhiteSpace(session.UserId))
            {
                return null;
            }
            return session;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(_config.DeviceId))
        {
            return;
        }
        _config.DeviceId = Guid.NewGuid().ToString("N");
        SaveConfig();
    }

    private void SaveConfig()
    {
        try
        {
            WriteJson(ConfigPath, _config);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Configuration Supabase", ex);
        }
    }

    private void SaveSession()
    {
        try
        {
            if (_session != null)
            {
                WriteJson(SessionPath, _session);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Session Supabase", ex);
        }
    }

    private string ConfigPath => Path.Combine(_store.Folder, "supabase.json");
    private string SessionPath => Path.Combine(_store.Folder, "supabase-session.json");

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), FileJson);
    }

    private static void WriteJson<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, FileJson));
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // a stale token file is harmless; it is ignored when no user is signed in
        }
    }

    private sealed class CloudConfig
    {
        public string Url { get; set; } = "";
        public string AnonKey { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public bool IsConfigured { get; set; }
    }

    private sealed class CloudSession
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public DateTime ExpiresAtUtc { get; set; }
        public string UserId { get; set; } = "";
        public string Email { get; set; } = "";
        public string Name { get; set; } = "";
        public string Provider { get; set; } = "";
    }

    private sealed class CloudRequestException : Exception
    {
        public CloudRequestException(string message) : base(message) { }
    }
}

internal sealed record CloudResult(bool Succeeded, string Message)
{
    public static CloudResult Ok(string message) => new(true, message);
    public static CloudResult Fail(string message) => new(false, message);
}

internal sealed record CloudSnapshot(
    bool Configured,
    bool SignedIn,
    string Email,
    string Name,
    string Provider,
    string Sync,
    DateTime? LastSyncUtc,
    string Error,
            bool Busy,
        string CallbackUrl,
        string RecoveryStage,
        string RecoveryEmail,
        string RecoveryError);
