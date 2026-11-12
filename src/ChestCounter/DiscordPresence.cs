using DiscordRPC;

namespace ChestCounter;

/// <summary>
/// Shows the counter as a Discord activity while the Discord desktop client is running.
/// The connection is local (Discord IPC): nothing is sent to a server, and no token is needed.
/// </summary>
internal static class DiscordPresence
{
    private static readonly object Sync = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly DateTime StartedAt = DateTime.UtcNow;

    private static readonly bool Debug = Environment.GetEnvironmentVariable("CHESTCOUNTER_DEBUG") == "discord";
    private static Thread? _thread;
    private static volatile bool _running;
    private static bool _enabled;
    private static string _details = "";
    private static string _state = "";

    /// <summary>Starts the background worker. Safe to call more than once.</summary>
    public static void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "DiscordPresence",
        };
        _thread.Start();
    }

    /// <summary>Publishes the latest figures. When disabled, the activity is cleared.</summary>
    public static void Update(bool enabled, string details, string state)
    {
        lock (Sync)
        {
            _enabled = enabled;
            _details = details;
            _state = state;
        }
        Wake.Set();
    }

    /// <summary>Disconnects and clears the activity before the application exits.</summary>
    public static void Stop()
    {
        _running = false;
        Wake.Set();
        var thread = _thread;
        _thread = null;
        if (thread != null && thread.IsAlive)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static void Loop()
    {
        DiscordRpcClient? client = null;
        var retryAt = DateTime.MinValue;
        var sent = "";

        try
        {
            while (_running)
            {
                bool enabled;
                string details, state;
                lock (Sync)
                {
                    enabled = _enabled;
                    details = _details;
                    state = _state;
                }

                if (!enabled || details.Length == 0)
                {
                    if (client != null)
                    {
                        Disconnect(client);
                        client = null;
                        sent = "";
                    }
                    Wake.WaitOne(TimeSpan.FromSeconds(5));
                    continue;
                }

                if (client == null && DateTime.UtcNow >= retryAt)
                {
                    DiscordRpcClient? candidate = null;
                    try
                    {
                        candidate = new DiscordRpcClient(AppInfo.DiscordClientId);
                        if (candidate.Initialize())
                        {
                            client = candidate;
                            candidate = null;
                            sent = "";
                            Log("Connected to Discord.");
                        }
                        else
                        {
                            Log("Discord refused the connection.");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Discord is not running yet, or the IPC pipe refused the connection.
                        Log("Connection failed: " + ex);
                        retryAt = DateTime.UtcNow.AddSeconds(15);
                    }
                    finally
                    {
                        candidate?.Dispose();
                    }

                    if (client == null)
                    {
                        retryAt = DateTime.UtcNow.AddSeconds(15);
                    }
                }

                if (client != null)
                {
                    if (!client.IsInitialized)
                    {
                        Disconnect(client);
                        client = null;
                        retryAt = DateTime.UtcNow.AddSeconds(15);
                    }
                    else
                    {
                        var signature = details + "\n" + state;
                        if (signature != sent)
                        {
                            try
                            {
                                client.SetPresence(new RichPresence
                                {
                                    Details = details,
                                    State = state,
                                    Assets = new Assets
                                    {
                                        LargeImageKey = AppInfo.GitHubIconUrl,
                                        LargeImageText = AppInfo.Name,
                                    },
                                    Buttons = new[]
                                    {
                                        new DiscordRPC.Button
                                        {
                                            Label = L.T("Suivre sur GitHub", "Follow on GitHub", "Seguir en GitHub"),
                                            Url = AppInfo.GitHubUrl,
                                        },
                                        new DiscordRPC.Button
                                        {
                                            Label = L.T("Télécharger", "Download", "Descargar"),
                                            Url = AppInfo.ReleasesUrl,
                                        },
                                    },
                                    Timestamps = new Timestamps { Start = StartedAt },
                                });
                                sent = signature;
                                Log("Presence sent: " + details + " | " + state);
                            }
                            catch (Exception ex)
                            {
                                Log("SetPresence failed: " + ex);
                                Disconnect(client);
                                client = null;
                                sent = "";
                                retryAt = DateTime.UtcNow.AddSeconds(15);
                            }
                        }
                    }
                }

                Wake.WaitOne(TimeSpan.FromSeconds(15));
            }
        }
        finally
        {
            if (client != null)
            {
                Disconnect(client);
            }
        }
    }

    private static void Log(string message)
    {
        if (Debug)
        {
            ErrorLog.Write("Discord", message);
        }
    }

    private static void Disconnect(DiscordRpcClient client)
    {
        try
        {
            if (client.IsInitialized)
            {
                client.ClearPresence();
            }
        }
        catch
        {
            // Discord was closed first: nothing left to clear.
        }

        try
        {
            client.Dispose();
        }
        catch
        {
            // already disconnected
        }
    }
}
