using MikuSB.Database;
using MikuSB.Proto;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.Packet.Recv.Login;

[Opcode(CmdIds.NtfSetStrAttr)]
public class HandlerNtfSetStrAttr : Handler
{
    private const int MaxLength = 4096;

    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        var req = NtfSetStrAttr.Parser.ParseFrom(data);
        var player = connection.Player;
        if (player == null) return;

        // String attributes also hold purchase limits, claim flags and run state, so writes are default deny.
        if (!ConfigManager.Config.ServerOption.ClientWritableStrAttrGroups.Contains(req.Gid) || req.Val.Length > MaxLength)
        {
            new Logger("NtfSetStrAttr").Warn($"Ignored client string attribute {req.Gid}/{req.Sid} ({req.Val.Length} chars)");
            return;
        }

        player.SetStrAttr(req.Gid, req.Sid, req.Val);
        DatabaseHelper.SaveDatabaseType(player.Data);

        await player.OnHeartBeat();
    }
}
