using MikuSB.Database;
using MikuSB.Util;
using MikuSB.Proto;

namespace MikuSB.GameServer.Server.Packet.Recv.Login;

[Opcode(CmdIds.NtfSetAttr)]
public class HandlerNtfSetAttr : Handler
{
    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        var req = NtfSetAttr.Parser.ParseFrom(data);
        var player = connection.Player;
        if (player == null)
        {
            // Client writes attributes during login; a session that has not finished logging in yet
            // (for example a reconnect that arrived while the server was still loading) must not crash.
            return;
        }

        // Default deny: wallets, purchases and progress must not be settable by a client packet.
        if (!ConfigManager.Config.ServerOption.ClientWritableAttrGroups.Contains(req.Gid))
        {
            new Logger("NtfSetAttr").Warn($"Ignored client write to server-owned attribute {req.Gid}/{req.Sid}");
            return;
        }

        player.Attributes.Set(req.Gid, req.Sid, req.Val);
        DatabaseHelper.SaveDatabaseType(player.Data);
        await player.OnHeartBeat();
    }
}
