using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MikuSB.Util;

namespace MikuSB.SdkServer.Utils;

public static class CertHelper
{
    private static readonly Logger Logger = new("SdkServer");
    private const int MaxDynamicCertificates = 64;
    private const string NameConstraintsOid = "2.5.29.30";
    private const string LegacyPassword = "MikuSB.SdkServer.LocalTLS";
    private static readonly Lazy<string> InstallPassword = new(LoadOrCreatePassword);
    private static string Password => InstallPassword.Value;
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

        // The name is chosen by an unauthenticated client, and each new one costs a key and files on disk.
        if (normalized.Length > 253 || !normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-'))
            return WildcardCert.Value;
        if (!Certificates.ContainsKey(normalized) && Certificates.Count >= MaxDynamicCertificates)
            return WildcardCert.Value;

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
            certificate = LoadPkcs12(File.ReadAllBytes(pfxPath), out var usedLegacyPassword);
            if (usedLegacyPassword)
                WritePrivate(pfxPath, certificate.Export(X509ContentType.Pfx, Password));
            if (!certificate.Extensions
                    .OfType<X509BasicConstraintsExtension>()
                    .Any(extension => extension.CertificateAuthority)
                || certificate.Extensions[NameConstraintsOid] == null)
            {
                // Older MikuSB releases made a self-signed server certificate
                // without CA or name constraints. Replace it so Linux trust tools can
                // include the certificate in their generated CA bundle.
                certificate.Dispose();
                using var generated = CreateSelfSigned(subjectHost);
                WritePrivate(pfxPath, generated.Export(X509ContentType.Pfx, Password));
                certificate = LoadPkcs12(File.ReadAllBytes(pfxPath));
            }
        }
        else
        {
            using var generated = CreateSelfSigned(subjectHost);
            WritePrivate(pfxPath, generated.Export(X509ContentType.Pfx, Password));
            certificate = LoadPkcs12(File.ReadAllBytes(pfxPath));
        }

        // Export only the public certificate. Recreate it from the PFX on startup so
        // existing installations get a PEM and it cannot become stale after renewal.
        File.WriteAllText(basePath + ".pem", certificate.ExportCertificatePem());
        return certificate;
    }

    // The PFX password is a constant, so file permissions are what protect the key.
    private static void WritePrivate(string path, byte[] data)
    {
        File.WriteAllBytes(path, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>A password per install, encrypted for the current user on Windows and owner-only elsewhere.</summary>
    private static string LoadOrCreatePassword()
    {
        Directory.CreateDirectory(CertificateDirectory);
        var path = Path.Combine(CertificateDirectory, "pfx-password.bin");
        if (File.Exists(path))
        {
            var stored = File.ReadAllBytes(path);
            return Convert.ToHexString(OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, null, DataProtectionScope.CurrentUser)
                : stored);
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        WritePrivate(path, OperatingSystem.IsWindows()
            ? ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser)
            : secret);
        return Convert.ToHexString(secret);
    }

    private static X509Certificate2 LoadPkcs12(byte[] pfx) => LoadPkcs12(pfx, out _);

    private static X509Certificate2 LoadPkcs12(byte[] pfx, out bool usedLegacyPassword)
    {
        usedLegacyPassword = false;
        try
        {
            return LoadPkcs12(pfx, Password);
        }
        catch (CryptographicException)
        {
            usedLegacyPassword = true;
            return LoadPkcs12(pfx, LegacyPassword);
        }
    }

    private static X509Certificate2 LoadPkcs12(byte[] pfx, string password)
    {
        // Persisted user key set on purpose -- do NOT "optimise" this to EphemeralKeySet.
        //
        // History (see docs/progress/021): when this server is started from a shell that may only write
        // inside the repository, persisting the key into the Windows user key store
        // (%APPDATA%\Microsoft\Crypto\...) is denied and the import throws "Access denied", which used
        // to kill the whole Kestrel/dispatch listener. EphemeralKeySet avoids that write, but on Windows
        // SChannel/SslStream cannot use an ephemeral private key: Kestrel still binds and the port shows
        // up as LISTENING, yet every TLS handshake dies with
        // "System.IO.IOException: Received an unexpected EOF or 0 bytes from the transport stream",
        // i.e. the client can never log in. A hard failure at startup (surfaced by SdkServer.Start's
        // try/catch) is much easier to diagnose than a listener that accepts nothing.
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        }
        // A wrong password fails the in-memory retry too, so the caller's legacy-password fallback still runs.
        catch (Exception ex)
        {
            // Some host processes cannot write the CNG key container at all (a restricted token, a
            // service account without a profile, a locked-down %APPDATA%). The message is localized, so
            // the fallback keys off "any failure", not an English/Access-denied check: without it the
            // whole SDK listener dies and the client has nothing to talk to. The in-memory key may
            // fail the handshake on SChannel, which then shows up as a TLS error in the client log -
            // still far better than a dead port with no explanation.
            Logger.Warn($"Could not persist the TLS key into the user key store ({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}); using an in-memory key instead.");
            return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        }
    }

    private static X509Extension BuildNameConstraints(string host)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        using (writer.PushSequence())
            writer.WriteCharacterString(UniversalTagNumber.IA5String, host.StartsWith("*.") ? host[2..] : host,
                new Asn1Tag(TagClass.ContextSpecific, 2));
        return new X509Extension(NameConstraintsOid, writer.Encode(), critical: true);
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
            hasPathLengthConstraint: true,
            pathLengthConstraint: 0,
            critical: true));

        // A trusted CA could otherwise vouch for any site: limit this one to its own host.
        req.CertificateExtensions.Add(BuildNameConstraints(host));

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
