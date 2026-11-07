using System.Security.Cryptography;
using System.Text;
using MikuSB.Enums.Player;
using MikuSB.Util;
using MikuSB.Util.Extensions;
using MikuSB.Util.Security;
using SqlSugar;

namespace MikuSB.Database.Account;

[SugarTable("Account")]
public class AccountData : BaseDatabaseDataHelper
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public BanTypeEnum BanType { get; set; }
    public string Phone { get; set; } = "";

    [SugarColumn(IsJson = true)] public List<PermEnum> Permissions { get; set; } = [];

    [SugarColumn(IsNullable = true)] public string? ComboToken { get; set; }
    [SugarColumn(IsNullable = true)] public string? DispatchToken { get; set; }

    #region GetAccount

    public static AccountData? GetAccountByUserName(string username)
    {
        AccountData? result = null;
        DatabaseHelper.GetAllInstance<AccountData>()?.ForEach(account =>
        {
            if (string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase)) result = account;
        });
        return result;
    }

    public static AccountData? GetAccountByEmail(string email)
        => GetAccountByUserName(email);

    public static AccountData? GetAccountByUid(int uid, bool force = false)
    {
        var result = DatabaseHelper.GetInstance<AccountData>(uid, force);
        return result;
    }

    public static AccountData? GetAccountByDispatchToken(string dispatchToken)
    {
        AccountData? result = null;
        DatabaseHelper.GetAllInstance<AccountData>()?.ForEach(account =>
        {
            if (account.DispatchToken == dispatchToken) result = account;
        });
        return result;
    }

    public static AccountData? GetAccountByComboToken(string comboToken)
    {
        AccountData? result = null;
        DatabaseHelper.GetAllInstance<AccountData>()?.ForEach(account =>
        {
            if (account.ComboToken == comboToken) result = account;
        });
        return result;
    }

    #endregion

    #region Account

    public static AccountData CreateAccount(string username, int uid, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be empty.", nameof(username));
        if (GetAccountByUserName(username) != null)
            throw new InvalidOperationException($"Account '{username}' already exists.");
        // The server profile UID is the built-in chat contact; a player sharing it would be unreachable.
        var reservedUid = ConfigManager.Config.ServerOption.ServerProfile.Uid;
        if (uid != 0 && uid == reservedUid)
            throw new InvalidOperationException($"UID '{uid}' is reserved for the server profile.");
        if (uid != 0 && GetAccountByUid(uid) != null)
            throw new InvalidOperationException($"UID '{uid}' is already in use.");

        var newUid = uid;
        if (uid == 0)
        {
            newUid = 1;
            while (newUid == reservedUid || GetAccountByUid(newUid) != null) newUid++;
        }

        var account = new AccountData
        {
            Uid = newUid,
            Username = username,
            Password = password,
            Phone = "123456",
            Permissions = [.. ConfigManager.Config.ServerOption.DefaultPermissions
                .Select(perm => Enum.TryParse(perm, out PermEnum result) ? result : (PermEnum?)null)
                .Where(result => result.HasValue).Select(result => result!.Value)]

        };
        SetPassword(account, password);

        DatabaseHelper.CreateInstance(account);
        return account;
    }

    public static void DeleteAccount(int uid)
    {
        if (GetAccountByUid(uid) == null) return;
        DatabaseHelper.DeleteAllInstance(uid);
    }

    private const int Pbkdf2Iterations = 210_000;

    public static void SetPassword(AccountData account, string password)
    {
        account.Password = password.Length > 0 ? HashPassword(password) : "";
    }

    /// <summary>Stores a new password and keeps the cached account row in step with the database.</summary>
    public static void ChangePassword(AccountData account, string password)
    {
        SetPassword(account, password);
        DatabaseHelper.UpdateInstance(account);
        var cached = DatabaseHelper.GetInstance<AccountData>(account.Uid);
        if (cached != null) cached.Password = account.Password;
    }

    public static bool VerifyPassword(AccountData account, string password)
    {
        var parts = account.Password.Split('$');
        if (parts.Length == 4 && parts[0] == "pbkdf2")
        {
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[2]),
                int.Parse(parts[1]), HashAlgorithmName.SHA512, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        // Older accounts hold an unsalted SHA-256: upgrade them on the first successful login.
        var legacy = Encoding.UTF8.GetBytes(Extensions.GetSha256Hash(password));
        if (account.Password.Length == 0
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(account.Password), legacy))
            return false;

        ChangePassword(account, password);
        return true;
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 32);
        return $"pbkdf2${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }


    #endregion

    #region Permission

    public static bool HasPerm(PermEnum[] perms, int uid)
    {
        if (uid == (int)ServerEnum.Console) return true;
        var account = GetAccountByUid(uid);
        if (account?.Permissions == null) return false;
        if (account.Permissions.Contains(PermEnum.Admin)) return true;

        return perms.Any(account.Permissions.Contains);
    }

    public static void AddPerm(PermEnum[] perms, int uid)
    {
        if (uid == (int)ServerEnum.Console) return;
        var account = GetAccountByUid(uid);
        if (account == null) return;

        account.Permissions ??= [];
        foreach (var perm in perms)
        {
            if (!account.Permissions.Contains(perm))
            {
                account.Permissions = [.. account.Permissions, perm];
            }
        }

        DatabaseHelper.UpdateInstance(account);
    }

    public static void RemovePerm(PermEnum[] perms, int uid)
    {
        if (uid == (int)ServerEnum.Console) return;
        var account = GetAccountByUid(uid);
        if (account == null) return;
        if (account.Permissions == null) return;

        foreach (var perm in perms)
        {
            if (account.Permissions.Contains(perm))
            {
                account.Permissions = account.Permissions.Except([perm]).ToList();
            }
        }

        DatabaseHelper.UpdateInstance(account);
    }

    public static void CleanPerm(int uid)
    {
        if (uid == (int)ServerEnum.Console) return;
        var account = GetAccountByUid(uid);
        if (account == null) return;

        account.Permissions = [];
    }

    #endregion

    #region Token

    public string GenerateDispatchToken()
    {
        DispatchToken = Crypto.CreateSessionKey(Uid.ToString());
        DatabaseHelper.UpdateInstance(this);
        SyncTokenCache();
        return DispatchToken;
    }

    public string GenerateComboToken()
    {
        ComboToken = Crypto.CreateSessionKey(Uid.ToString());
        DatabaseHelper.UpdateInstance(this);
        SyncTokenCache();
        return ComboToken;
    }

    /// <summary>
    /// Mirrors the freshly minted tokens into the instance held by <c>DatabaseHelper.UidInstanceMap</c>.
    ///
    /// The SDK login path resolves the account with <see cref="GetAccountByUserName"/>, which reads a
    /// NEW detached row out of the database instead of handing back the cached instance. Minting a
    /// token on that copy wrote the new value to the database while the cache kept the old one, and
    /// <see cref="DatabaseHelper.SaveDatabase"/> serialises the CACHED instance for every uid that
    /// logged a heartbeat (<c>PlayerInstance.OnHeartBeat</c> -> <c>ToSaveUidList</c>). The periodic
    /// 5-minute save therefore reverted <c>ComboToken</c> to its pre-login value, after which
    /// <c>HandlerReqLogin</c> could no longer match the token the client had just been given and
    /// answered <c>NtfLogout</c> - the client sat on "识别中" forever. Keeping the cache in step with
    /// every mint removes that writer of stale tokens.
    /// </summary>
    private void SyncTokenCache()
    {
        var cached = DatabaseHelper.GetInstance<AccountData>(Uid);
        if (cached == null || ReferenceEquals(cached, this))
            return;

        cached.ComboToken = ComboToken;
        cached.DispatchToken = DispatchToken;
    }

    #endregion
}
