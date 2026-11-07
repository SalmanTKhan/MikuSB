using System.Security.Cryptography;

namespace MikuSB.Util.Security;

/// <summary>Checks release packages against the project's public key. The private key never enters the repo.</summary>
public static class ReleaseSignature
{
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEo5nHxMQTZX6X1b6M30+wZbxclmCSkvY7sZg5fvIjbnR2cgB0pQK9HneZFDP/s6w0Q7fbzh+u5XX3/8S0bI6vEg==";

    public static bool Verify(byte[] data, string base64Signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            return key.VerifyData(data, Convert.FromBase64String(base64Signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
