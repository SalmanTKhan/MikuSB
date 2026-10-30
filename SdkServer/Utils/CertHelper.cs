using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MikuSB.SdkServer.Utils;

public static class CertHelper
{
    private const string Password = "MikuSB.SdkServer.LocalTLS";
    private static readonly ConcurrentDictionary<string, X509Certificate2> Certificates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<X509Certificate2> WildcardCert =
        new(() => LoadOrCreatePersisted("wildcard.xoyo.games", "*.xoyo.games"));

    private static string CertificateDirectory => Path.Combine(AppContext.BaseDirectory, "sdk-certs");

    public static X509Certificate2 GetOrCreate(string? serverName)
    {
        if (string.IsNullOrEmpty(serverName) ||
            serverName.EndsWith(".xoyo.games", StringComparison.OrdinalIgnoreCase))
            return WildcardCert.Value;

        var normalized = serverName.Trim().TrimEnd('.').ToLowerInvariant();
        return Certificates.GetOrAdd(normalized, host => LoadOrCreatePersisted(host, host));
    }

    private static X509Certificate2 LoadOrCreatePersisted(string fileHost, string subjectHost)
    {
        Directory.CreateDirectory(CertificateDirectory);
        var basePath = Path.Combine(CertificateDirectory, SanitizeFileName(fileHost));
        var pfxPath = basePath + ".pfx";
        X509Certificate2 certificate;
        if (File.Exists(pfxPath))
        {
            certificate = LoadPkcs12(File.ReadAllBytes(pfxPath));
            if (!certificate.Extensions
                    .OfType<X509BasicConstraintsExtension>()
                    .Any(extension => extension.CertificateAuthority))
            {
                // Older MikuSB releases made a self-signed server certificate
                // without CA constraints. Replace it so Linux trust tools can
                // include the certificate in their generated CA bundle.
                certificate.Dispose();
                using var generated = CreateSelfSigned(subjectHost);
                File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, Password));
                certificate = LoadPkcs12(File.ReadAllBytes(pfxPath));
            }
        }
        else
        {
            using var generated = CreateSelfSigned(subjectHost);
            File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, Password));
            certificate = LoadPkcs12(File.ReadAllBytes(pfxPath));
        }

        // Export only the public certificate. Recreate it from the PFX on startup so
        // existing installations get a PEM and it cannot become stale after renewal.
        File.WriteAllText(basePath + ".pem", certificate.ExportCertificatePem());
        return certificate;
    }

    private static X509Certificate2 LoadPkcs12(byte[] pfx)
    {
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            Password,
            X509KeyStorageFlags.UserKeySet |
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 CreateSelfSigned(string host)
    {
        using var rsa = RSA.Create(2048);

        var req = new CertificateRequest(
            new X500DistinguishedName($"CN={host}"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());

        // These self-signed certificates are installed as trust anchors by Linux
        // clients. Mark them as CAs so p11-kit includes them in the generated
        // system CA bundle used by OpenSSL-based clients such as curl.
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true,
            hasPathLengthConstraint: false,
            pathLengthConstraint: 0,
            critical: true));

        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment |
            X509KeyUsageFlags.DigitalSignature |
            X509KeyUsageFlags.KeyCertSign |
            X509KeyUsageFlags.CrlSign,
            critical: true));

        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
            critical: false));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        var pfx = cert.Export(X509ContentType.Pfx, Password);
        return LoadPkcs12(pfx);
    }

    private static string SanitizeFileName(string host)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(host.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }
}
