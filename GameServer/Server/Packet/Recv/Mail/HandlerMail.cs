using MikuSB.GameServer.Game.Mail;
using MikuSB.GameServer.Server.CallGS;
using MikuSB.Proto;

namespace MikuSB.GameServer.Server.Packet.Recv.Mail;

internal static class MailPackets
{
    /// <summary>The mail ids of a request; the first one is the mail asked about.</summary>
    public static ulong Mid(byte[] data)
    {
        var ids = IDArray.Parser.ParseFrom(data).Ids;
        return ids.Count > 0 ? ids[0] : 0;
    }
}

[Opcode(CmdIds.ReqReadMail)]
public class HandlerReqReadMail : Handler
{
    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        var mail = connection.Player?.MailManager.Read(MailPackets.Mid(data));
        await connection.SendPacket(CmdIds.RspReadMail, mail == null ? new PlayerMail() : MailManager.ToBox(mail));
    }
}

[Opcode(CmdIds.ReqMailAttachment)]
public class HandlerReqMailAttachment : Handler
{
    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        if (connection.Player is not { } player) return;
        var sync = new NtfSyncPlayer();
        var mail = await player.MailManager.Claim(MailPackets.Mid(data), sync);
        // The grant's state goes first, through the empty-name script every client applies, so the reply finds it in place.
        if (mail != null) await CallGSRouter.SendScript(connection, "", "{}", sync);
        await connection.SendPacket(CmdIds.RspMailAttachment, mail == null ? new PlayerMail() : MailManager.ToBox(mail));
    }
}

[Opcode(CmdIds.ReqDelMail)]
public class HandlerReqDelMail : Handler
{
    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        if (connection.Player is not { } player) return;
        var mail = player.MailManager.Delete(MailPackets.Mid(data));
        if (mail == null) { await connection.SendPacket(CmdIds.RspDelMail, new PlayerMail()); return; }
        await connection.SendPacket(CmdIds.NtfSyncDelMail, new IDArray { Ids = { mail.Mid } });
        await connection.SendPacket(CmdIds.RspDelMail, MailManager.ToBox(mail));
    }
}
