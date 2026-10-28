using MikuSB.Proto;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.Packet.Recv.Login;

// Sent by the client after LanguageChange. The emulator runs a single world channel (RspLogin.WorldChannel=1),
// so there is nothing to switch; the request message has no schema yet, so its payload is logged for later
// decoding and an empty RspChangeWorldChannel is returned. Not observed in the retail capture.
[Opcode(CmdIds.ReqChangeWorldChannel)]
public class HandlerReqChangeWorldChannel : Handler
{
    public override async Task OnHandle(Connection connection, byte[] data, ushort seqNo)
    {
        Logger.GetByClassName().Debug($"ReqChangeWorldChannel payload={Convert.ToHexString(data)}");
        await connection.SendPacket(CmdIds.RspChangeWorldChannel);
    }
}
