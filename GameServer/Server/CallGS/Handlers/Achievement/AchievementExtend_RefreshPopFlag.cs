using MikuSB.Data;
using MikuSB.Database;
using MikuSB.Proto;
using System.Text.Json.Serialization;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Achievement;

// The client reports the achievements whose pop-up it has shown. AchievementExtend.GetPopFlag reads the flag
// back from group 111, attribute floor(nId / 30) + 1 (at most 2200), bit nId % 30, and shows the pop-up
// while that bit is 0. Without this record, every finished but unclaimed achievement pops up again.
// param: {tbList}
[CallGSApi("AchievementExtend_RefreshPopFlag")]
public class AchievementExtend_RefreshPopFlag : CallGSHandler<AchievementPopFlagParam>
{
    private const uint PopGroupId = 111;
    private const uint FlagsPerAttribute = 30;
    private const uint MaxPopSid = 2200;

    protected override Task<CallGSResult> HandleAsync(CallGSContext context, AchievementPopFlagParam request)
    {
        var player = context.Player;
        var sync = new NtfSyncPlayer();
        var changed = false;

        foreach (var id in request.Ids.Distinct())
        {
            var sid = id / FlagsPerAttribute + 1;
            if (sid > MaxPopSid || !GameData.AchievementData.ContainsKey(id))
                continue;

            var attr = player.Attributes.GetOrCreate(PopGroupId, sid);
            var bit = 1u << (int)(id % FlagsPerAttribute);
            if ((attr.Val & bit) != 0)
                continue;

            attr.Val |= bit;
            player.Attributes.SyncTo(sync, attr);
            changed = true;
        }

        if (changed)
            DatabaseHelper.SaveDatabaseType(player.Data);

        return Task.FromResult(CallGSResult.Ok("{}", sync, "AchievementExtend_RefreshPopFlag"));
    }
}

public sealed class AchievementPopFlagParam
{
    [JsonPropertyName("tbList")] public List<uint> Ids { get; set; } = [];
}
