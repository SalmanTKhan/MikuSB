namespace MikuSB.GameServer.Server.CallGS.Handlers.Activity;

// Client requests an activity state refresh. No s2c callback registered on client side.
// param: {nId}
[CallGSApi("Activity_Refresh")]
public class Activity_Refresh : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        // TODO: refresh activity state for the given nId
        var root = System.Text.Json.Nodes.JsonNode.Parse(param);
        var id = CapturedGameplay.UInt(root, "nId");
        if (id == 0) return Task.FromResult(CallGSResult.Error("error.BadParam"));
        return Task.FromResult(CallGSResult.Ok(new System.Text.Json.Nodes.JsonObject { ["nId"] = id }, api: "ActivityQuest_Refresh"));
    }
}
