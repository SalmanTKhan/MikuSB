using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Achievement;

// Client requests a reward for a completed achievement.
// param: {nId}
// Response: {tbRewards}
[CallGSApi("Achievement_GetReward")]
public class Achievement_GetReward : CallGSHandler<AchievementRewardParam>
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, AchievementRewardParam request)
    {
        var (rewards, sync) = await context.Player.AchievementManager.ClaimAsync([request.Id], []);
        return CallGSResult.Ok(new JsonObject { ["tbRewards"] = rewards }, sync);
    }
}

// param: {tbIdList, nType, tbPointList}
// Response: {tbRewards}
[CallGSApi("Achievement_QuickGetReward")]
public class Achievement_QuickGetReward : CallGSHandler<AchievementQuickRewardParam>
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, AchievementQuickRewardParam request)
    {
        var (rewards, sync) = await context.Player.AchievementManager.ClaimAsync(request.Ids, request.Points);
        return CallGSResult.Ok(new JsonObject { ["tbRewards"] = rewards }, sync);
    }
}

public sealed class AchievementRewardParam
{
    [JsonPropertyName("nId")] public uint Id { get; set; }
}

public sealed class AchievementQuickRewardParam
{
    [JsonPropertyName("tbIdList")] public List<uint> Ids { get; set; } = [];
    [JsonPropertyName("tbPointList")] public List<int> Points { get; set; } = [];
}
