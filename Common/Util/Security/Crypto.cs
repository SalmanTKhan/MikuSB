using System.Security.Cryptography;
using System.Text;

namespace MikuSB.Util.Security;

public class Crypto
{
    // 64 bytes from the OS generator; hashed so the token keeps its existing length and format.
    public static string CreateSessionKey(string accountUid)
    {
        var bytes = SHA512.HashData(RandomNumberGenerator.GetBytes(64));
        return Convert.ToBase64String(bytes);
    }
}
