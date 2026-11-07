using System.Security.Cryptography;
using MikuSB.Util.Security;

// generate <private key file>  : creates a key pair and prints the public key to embed in ReleaseSignature.
// sign <file>                  : writes <file>.sig using the PEM key in RELEASE_SIGNING_KEY or MIKUSB_SIGNING_KEY_FILE.
// verify <file>                : checks <file>.sig against the embedded public key.
switch (args)
{
    case ["generate", var keyPath]:
    {
        if (File.Exists(keyPath)) throw new IOException($"{keyPath} already exists.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }
    case ["sign", var file]:
    {
        var pem = Environment.GetEnvironmentVariable("RELEASE_SIGNING_KEY");
        if (string.IsNullOrWhiteSpace(pem) && Environment.GetEnvironmentVariable("MIKUSB_SIGNING_KEY_FILE") is { Length: > 0 } keyFile)
            pem = File.ReadAllText(keyFile);
        if (string.IsNullOrWhiteSpace(pem)) throw new InvalidOperationException("No signing key was supplied.");
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var signature = key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256);
        File.WriteAllText(file + ".sig", Convert.ToBase64String(signature));
        return 0;
    }
    case ["verify", var file]:
    {
        var valid = ReleaseSignature.Verify(File.ReadAllBytes(file), File.ReadAllText(file + ".sig"));
        Console.WriteLine(valid ? "valid" : "INVALID");
        return valid ? 0 : 1;
    }
    default:
        Console.Error.WriteLine("usage: generate <key file> | sign <file> | verify <file>");
        return 2;
}
