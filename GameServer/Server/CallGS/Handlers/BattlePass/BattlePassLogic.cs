using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MikuSB.GameServer.Game.BattlePass;
using MikuSB.Proto;

namespace MikuSB.GameServer.Server.CallGS.Handlers.BattlePass;

// BattlePass.lua's calls. Every reply reaches umg_bpmain's OnReceiveUpdate, which shows tbAwards when present.

[CallGSApi("BattlePassLogic_FirstOpen")]
public class BattlePassLogic_FirstOpen : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var sync = new NtfSyncPlayer();
        BattlePassService.Refresh(context.Player, sync);
        BattlePassService.MarkOpened(context.Player, sync);
        return Task.FromResult(CallGSResult.Ok("{}", sync));
    }
}

[CallGSApi("BattlePassLogic_GetPassAward")]
public class BattlePassLogic_GetPassAward : CallGSHandler
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var (awards, sync) = await BattlePassService.ClaimLevels(context.Player);
        return CallGSResult.Ok(new JsonObject { ["tbAwards"] = awards }, sync);
    }
}

[CallGSApi("BattlePassLogic_QuickGetReward")]
public class BattlePassLogic_QuickGetReward : CallGSHandler<BattlePassQuickParam>
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, BattlePassQuickParam request)
    {
        // Rows are {kind, quest id} (BattlePass.CheckGetAllMissionList; uw_bpmission).
        var ids = (request.List ?? []).Where(r => r is { Count: >= 2 }).Select(r => r[1]);
        var (awards, sync) = await BattlePassService.ClaimMissions(context.Player, ids);
        return CallGSResult.Ok(new JsonObject { ["tbAwards"] = awards }, sync);
    }
}

[CallGSApi("BattlePassLogic_BuyLevel")]
public class BattlePassLogic_BuyLevel : CallGSHandler<BattlePassBuyLevelParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, BattlePassBuyLevelParam request)
    {
        if (request.Level == 0) return Task.FromResult(BadParam());
        var sync = new NtfSyncPlayer();
        var error = BattlePassService.BuyLevels(context.Player, request.Level, sync);
        return Task.FromResult(error != null ? CallGSResult.Error(error, sync) : CallGSResult.Ok("{}", sync));
    }
}

public sealed class BattlePassQuickParam
{
    [JsonPropertyName("tbIdList")] public List<List<uint>>? List { get; set; }
}

public sealed class BattlePassBuyLevelParam
{
    [JsonPropertyName("nLevel")] public uint Level { get; set; }
}
