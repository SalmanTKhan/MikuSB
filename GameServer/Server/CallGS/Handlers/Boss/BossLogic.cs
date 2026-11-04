using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MikuSB.GameServer.Game.Boss;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Boss;

// The retail client's Challenge/Boss/Boss.lua calls.
[CallGSApi("BossLogic_GetOpenID")]
public class BossLogic_GetOpenID : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var (id, sync) = context.Player.BossManager.Open();
        return Task.FromResult(CallGSResult.Ok(new JsonObject { ["nID"] = id }.ToJsonString(), sync));
    }
}

[CallGSApi("BossLogic_EnterLevel")]
public class BossLogic_EnterLevel : CallGSHandler<BossEnterParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, BossEnterParam request)
    {
        var (error, seed, sync) = context.Player.BossManager.Enter(request.Id, request.Team, request.Difficulty, request.Entries);
        return Task.FromResult(error != null ? CallGSResult.Error(error) : CallGSResult.Ok(new JsonObject { ["nSeed"] = seed }.ToJsonString(), sync));
    }
}

[CallGSApi("BossLogic_LevelSettlement")]
public class BossLogic_LevelSettlement : CallGSHandler<BossSettleParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, BossSettleParam request)
    {
        var (error, score, best, sync) = context.Player.BossManager.Settle(request.Id, request.Seed, request.ExitNum);
        return Task.FromResult(error != null ? CallGSResult.Error(error) : CallGSResult.Ok(new JsonObject { ["nScore"] = score, ["nBest"] = best }.ToJsonString(), sync));
    }
}

[CallGSApi("BossLogic_LevelFail")]
public class BossLogic_LevelFail : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        context.Player.BossManager.Fail();
        return Task.FromResult(CallGSResult.Ok("{}"));
    }
}

[CallGSApi("BossLogic_Record")]
public class BossLogic_Record : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param) => Task.FromResult(CallGSResult.Ok(param));
}

[CallGSApi("BossLogic_Reset")]
public class BossLogic_Reset : CallGSHandler<BossIdParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, BossIdParam request) =>
        Task.FromResult(CallGSResult.Ok("{}", context.Player.BossManager.Reset(request.Id)));
}

[CallGSApi("BossLogic_GetReward")]
public class BossLogic_GetReward : CallGSHandler<BossRewardParam>
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, BossRewardParam request)
    {
        var (rewards, sync) = await context.Player.BossManager.ClaimAsync(request.Mileage);
        return CallGSResult.Ok(new JsonObject { ["tbAward"] = rewards }.ToJsonString(), sync, "BossLogic_GetReward");
    }
}

[CallGSApi("BossLogic_OneClicReward")]
public class BossLogic_OneClicReward : CallGSHandler
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var (rewards, sync) = await context.Player.BossManager.ClaimAsync(0);
        return CallGSResult.Ok(new JsonObject { ["tbAward"] = rewards }.ToJsonString(), sync, "BossLogic_GetReward");
    }
}

public sealed class BossIdParam
{
    [JsonPropertyName("nID")] public uint Id { get; set; }
}

public sealed class BossEnterParam
{
    [JsonPropertyName("nID")] public uint Id { get; set; }
    [JsonPropertyName("nTeamID")] public int Team { get; set; }
    [JsonPropertyName("nDifficulty")] public int Difficulty { get; set; }
    [JsonPropertyName("tbEntrie")] public List<uint> Entries { get; set; } = [];
}

public sealed class BossSettleParam
{
    [JsonPropertyName("nID")] public uint Id { get; set; }
    [JsonPropertyName("nSeed")] public int Seed { get; set; }
    [JsonPropertyName("nExitNum")] public int ExitNum { get; set; }
}

public sealed class BossRewardParam
{
    [JsonPropertyName("Mileage")] public uint Mileage { get; set; }
}
