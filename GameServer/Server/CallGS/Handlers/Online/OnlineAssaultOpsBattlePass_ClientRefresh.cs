namespace MikuSB.GameServer.Server.CallGS.Handlers.Online;

// Not in the retail capture. Mirrors BattlePassLogic_ClientRefresh, which retail answered with [] every time;
// the assault-ops battle pass id itself is set through OnlineAssaultOpsBattlePass_ChangeBP's custom attribute.
[CallGSApi("OnlineAssaultOpsBattlePass_ClientRefresh")]
public class OnlineAssaultOpsBattlePass_ClientRefresh : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        return Task.FromResult(CallGSResult.Ok("[]"));
    }
}
