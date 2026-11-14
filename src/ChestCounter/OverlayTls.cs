using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChestCounter;

/// <summary>
/// TLS material for the local stream overlay. TikTok LIVE Studio only accepts https:// links, so the
/// overlay is also served over TLS on the loopback interface. The certificate is anyip.dev's public
/// wildcard certificate: 127-0-0-1.anyip.dev resolves to 127.0.0.1, so nothing is exposed to the
/// internet and no certificate has to be installed in Windows. The certificate and its public
/// development key are cached in the data folder and refreshed only when they are about to expire.
/// </summary>
internal sealed class OverlayCertificate : IDisposable
{
    private const string ChainFileName = "overlay-tls-chain.pem";
    private const string KeyFileName = "overlay-tls-key.pem";
    private const string ChainUrl = "https://anyip.dev/cert/fullchain.pem";
    private const string KeyUrl = "https://anyip.dev/cert/privkey.pem";

    public X509Certificate2 Certificate { get; }
    public SslStreamCertificateContext Context { get; }

    private OverlayCertificate(X509Certificate2 certificate, SslStreamCertificateContext context)
    {
        Certificate = certificate;
        Context = context;
    }

    /// <summary>Reads the cached certificate, downloading it only when missing or close to expiry.</summary>
    public static OverlayCertificate? Load(string folder)
    {
        var chainPath = Path.Combine(folder, ChainFileName);
        var keyPath = Path.Combine(folder, KeyFileName);
        var cached = Read(chainPath, keyPath);
        if (cached != null && Expiry(cached) > DateTime.UtcNow.AddDays(7))
        {
            return cached;
        }

        var fresh = Download(chainPath, keyPath);
        if (fresh != null)
        {
            cached?.Dispose();
            return fresh;
        }

        if (cached != null && Expiry(cached) > DateTime.UtcNow)
        {
            return cached;
        }

        cached?.Dispose();
        return null;
    }

    private static OverlayCertificate? Read(string chainPath, string keyPath)
    {
        try
        {
            if (!File.Exists(chainPath) || !File.Exists(keyPath))
            {
                return null;
            }
            return Create(File.ReadAllText(chainPath), File.ReadAllText(keyPath));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Overlay HTTPS cache", ex);
            return null;
        }
    }

    private static OverlayCertificate? Download(string chainPath, string keyPath)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var chain = client.GetStringAsync(ChainUrl).GetAwaiter().GetResult();
            var key = client.GetStringAsync(KeyUrl).GetAwaiter().GetResult();
            var loaded = Create(chain, key);
            if (loaded != null && Expiry(loaded) > DateTime.UtcNow.AddDays(3))
            {
                try
                {
                    File.WriteAllText(chainPath, chain);
                    File.WriteAllText(keyPath, key);
                }
                catch (Exception ex)
                {
                    // The certificate still works for this session even if the cache cannot be written.
                    ErrorLog.Write("Overlay HTTPS cache write", ex);
                }
                return loaded;
            }
            loaded?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Overlay HTTPS certificate refresh", ex);
        }
        return null;
    }

    private static OverlayCertificate? Create(string chainPem, string keyPem)
    {
        try
        {
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPem(chainPem);
            if (certificates.Count == 0)
            {
                return null;
            }

            using var key = ECDsa.Create();
            key.ImportFromPem(keyPem);
            using var source = certificates[0].CopyWithPrivateKey(key);
            var pfx = source.Export(X509ContentType.Pfx);
            // UserKeySet: SslStream on Windows cannot use ephemeral keys for a server certificate.
            var leaf = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            var chain = new X509Certificate2Collection();
            for (var i = 1; i < certificates.Count; i++)
            {
                chain.Add(certificates[i]);
            }

            var context = SslStreamCertificateContext.Create(leaf, chain, offline: true);
            return new OverlayCertificate(leaf, context);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Overlay HTTPS certificate", ex);
            return null;
        }
    }

    private static DateTime Expiry(OverlayCertificate certificate) => certificate.Certificate.NotAfter.ToUniversalTime();

    public void Dispose()
    {
        try { Certificate.Dispose(); } catch { /* best effort */ }
    }
}
