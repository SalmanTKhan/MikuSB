using System.Collections.Concurrent;
using System.Security.Cryptography;
using MikuSB.Proto;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.Packet.Recv.Login;

/// <summary>The session ids RspLogin hands out: the proof ReqReconnect carries instead of the SDK token. Valid for this server's life.</summary>
public static class Sessions
{
    private static readonly ConcurrentDictionary<int, string> ByUid = new();

    public static string Issue(int uid) => ByUid[uid] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static bool Valid(int uid, string? session) =>
        !string.IsNullOrEmpty(session) && ByUid.TryGetValue(uid, out var known) &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(known), System.Text.Encoding.ASCII.GetBytes(session));
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
        var previous = Listener.GetActiveConnection(uid);
        if (previous != null && !ReferenceEquals(previous, connection)) previous.Stop();
        await HandlerReqLogin.EnterAsync(connection, uid, req.SessionId);
    }
}
