namespace MikuSB.GameServer.Server.CallGS.Handlers.Misc;

// Client telemetry ({sLog}); like the other *Log APIs in the retail capture it has no Lua callback.
[CallGSApi("GirlRoomLog")]
public class GirlRoomLog : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        return Task.FromResult(CallGSResult.NoResponse());
    }
}
