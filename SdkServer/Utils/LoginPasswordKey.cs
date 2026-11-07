using System.Security.Cryptography;
using MikuSB.Util;

namespace MikuSB.SdkServer.Utils;

/// <summary>
/// RSA key behind /seasun/config accountPublicKey. The SDK adds the PEM header and line breaks itself, so it
/// must receive bare base64. With an unusable key it leaves the password out of /seasun/login. It encrypts
/// 117 bytes at a time with PKCS#1 padding and base64-encodes the joined blocks.
/// </summary>
public static class LoginPasswordKey
{
    private static readonly Lazy<RSA> Key = new(LoadOrCreate);

    public static string PublicKeyBase64 => Convert.ToBase64String(Key.Value.ExportSubjectPublicKeyInfo());

    /// <summary>Returns the password, or null when the value was not encrypted with this key.</summary>
    public static string? Decrypt(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        try
        {
            var cipher = Convert.FromBase64String(value);
            var blockSize = Key.Value.KeySize / 8;
            if (cipher.Length == 0 || cipher.Length % blockSize != 0)
                return null;

            var plain = cipher.Chunk(blockSize)
                .SelectMany(block => Key.Value.Decrypt(block, RSAEncryptionPadding.Pkcs1))
                .ToArray();
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static RSA LoadOrCreate()
    {
        var path = Path.Combine(Path.GetFullPath(ConfigManager.Config.Path.ConfigPath), "login-password-key.pem");
        var rsa = RSA.Create(2048);
        if (File.Exists(path))
        {
            rsa.ImportFromPem(File.ReadAllText(path));
            return rsa;
        }

        File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return rsa;
    }
}
