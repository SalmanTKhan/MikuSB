using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MikuSB.Database.Account;
using MikuSB.Enums.Player;
using MikuSB.Proto;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.Packet.Recv.Login;

/// <summary>
/// The session ids RspLogin hands out: the proof ReqReconnect carries instead of the SDK token. Expire after a
/// day, or at the next login. Only their hashes are saved, so a client can reconnect after a server restart
/// and the file cannot be used to log in.
/// </summary>
public static class Sessions
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private static readonly Lock SaveLock = new();
    private static readonly Lazy<ConcurrentDictionary<int, SavedSession>> ByUid = new(Load);

    private static string StorePath => Path.Combine(Path.GetFullPath(ConfigManager.Config.Path.ConfigPath), "sessions.json");

    public static string Issue(int uid)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        ByUid.Value[uid] = new SavedSession(Hash(id), DateTime.UtcNow);
        Save();
        return id;
    }

    public static bool Valid(int uid, string? session) =>
        !string.IsNullOrEmpty(session) && ByUid.Value.TryGetValue(uid, out var known) &&
        DateTime.UtcNow - known.IssuedAt < Lifetime &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(known.Hash), Convert.FromHexString(Hash(session)));

    private static string Hash(string session) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(session)));

    private static ConcurrentDictionary<int, SavedSession> Load()
    {
        try
        {
            if (File.Exists(StorePath) &&
                JsonSerializer.Deserialize<Dictionary<int, SavedSession>>(File.ReadAllText(StorePath)) is { } saved)
                return new ConcurrentDictionary<int, SavedSession>(
                    saved.Where(entry => DateTime.UtcNow - entry.Value.IssuedAt < Lifetime));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            new Logger("ReqReconnect").Warn($"Could not read saved sessions: {ex.Message}");
        }

        return new ConcurrentDictionary<int, SavedSession>();
    }

    private static void Save()
    {
        try
        {
            lock (SaveLock)
                File.WriteAllText(StorePath, JsonSerializer.Serialize(ByUid.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            new Logger("ReqReconnect").Warn($"Could not save sessions: {ex.Message}");
        }
    }

    private sealed record SavedSession(string Hash, DateTime IssuedAt);
}

// ReqReconnect {pid, session_id, world_channel, language} -> RspReconnect {session_id, data, ...}; an unknown session gets NtfLogout, as a bad token does.
[Opcode(CmdIds.ReqReconnect)]
public class HandlerReqReconnect : Handler
{
    private static readonly Logger Logger = new("ReqReconnect");

    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        var req = ReqReconnect.Parser.ParseFrom(data);
        if (req.Pid > int.MaxValue || !Sessions.Valid((int)req.Pid, req.SessionId))
        {
            Logger.Warn($"Rejected reconnect: pid={req.Pid}");
            await connection.SendPacket(CmdIds.NtfLogout);
            return;
        }
        int uid = (int)req.Pid;
        if (AccountData.GetAccountByUid(uid)?.BanType is not (null or BanTypeEnum.None))
        {
            Logger.Warn($"Rejected reconnect: uid={uid} is banned.");
            await connection.SendPacket(CmdIds.NtfLogout);
            return;
        }

        var previous = Listener.GetActiveConnection(uid);
        if (previous != null && !ReferenceEquals(previous, connection)) previous.Stop();
        await HandlerReqLogin.EnterAsync(connection, uid, req.SessionId);
    }
}
