namespace MikuSB.GameServer.Server.CallGS.Handlers.Activity;

// Updates the lobby activity face (presentation effect).
// param: {nType(1=equip, 2=unequip, 3=set flag), nId, bFlag}
// Response: {nFaceId, nId, nType} — nFaceId=0 means no next face
[CallGSApi("ActivityFace_Update")]
public class ActivityFace_Update : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(param);
        var id = CapturedGameplay.UInt(root, "nId");
        var type = CapturedGameplay.UInt(root, "nType");
        if (id == 0 || type is < 1 or > 3) return Task.FromResult(CallGSResult.Error("error.BadParam"));
        var state = MikuSB.Database.DatabaseHelper.GetInstanceOrCreateNew<MikuSB.Database.Player.PreservationProgressData>(context.Player.Uid);
        state.Values[$"activity:{id}:face"] = type.ToString();
        MikuSB.Database.DatabaseHelper.SaveDatabaseType(state);
        return Task.FromResult(CallGSResult.Ok(new System.Text.Json.Nodes.JsonObject { ["nId"] = id, ["nType"] = type }));
    }
}
