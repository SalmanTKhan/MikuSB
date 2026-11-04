using System.Text.Json.Nodes;
using MikuSB.Data;
using MikuSB.Data.Excel;
using MikuSB.Database;
using MikuSB.GameServer.Game.Player;
using MikuSB.Proto;

namespace MikuSB.GameServer.Game.Quest;

/// <summary>
/// Task progress in the attributes the client reads (Achievement.lua): gid 2, sid = task id, bit 0 claimed, bits 1-31 progress; daily points gid 8
/// sid 1, their milestones claimed sid 10-19. Daily tasks reset at 04:00 UTC+8, weekly ones on Monday 04:00. Progress comes from the player's state
/// (level, owned items, cleared stages) or from counted calls; enemy kills are not reported to the server, so types 77 and 18 stay at 0.
/// </summary>
public sealed class AchievementManager(PlayerInstance player) : BasePlayerManager(player)
{
    private const uint Gid = AttrIds.Achievement.GroupId;
    private const uint PointGid = AttrIds.Achievement.DailyPointGroupId;
    private const uint PointSid = AttrIds.Achievement.DailyPointSid;
    private const uint RewardSid = AttrIds.Achievement.DailyRewardStartSid;
    private const uint DailyWindowSid = 2, WeeklyWindowSid = 3;
    private const int Daily = 1, Weekly = 2, BranchLine = 4, ChapterReward = 5;

    /// <summary>The calls a counted type follows; a call counts when the server synced a change back.</summary>
    private static readonly Dictionary<string, int> CountedCalls = new()
    {
        ["Chapter_DealLevelSettlement"] = 14,
        ["Daily_DealLevelSettlement"] = 14,
        ["Role_DealLevelSettlement"] = 14,
        ["IBLogic_BuyGoods"] = 8,
        ["GirlCard_UpdateLevel"] = 21,
        ["Weapon_Upgrade"] = 38,
        ["SupporterCard_Upgrade"] = 44,
    };

    private uint Progress(AchievementExcel t) => Player.Attributes.GetValue(Gid, t.ID) >> 1;
    private bool Claimed(AchievementExcel t) => (Player.Attributes.GetValue(Gid, t.ID) & 1) != 0;

    private void Write(uint gid, uint sid, uint value, NtfSyncPlayer sync)
    {
        if (Player.Attributes.GetValue(gid, sid) == value) return;
        Player.Attributes.Set(gid, sid, value);
        Player.Attributes.SyncTo(sync, gid, sid, value);
    }

    private static long Window(int group, DateTime utc)
    {
        long days = (long)(utc + TimeSpan.FromHours(4) - DateTime.UnixEpoch).TotalDays;
        return group == Daily ? days : (days + 3) / 7;   // 1970-01-01 was a Thursday
    }

    private void Reset(NtfSyncPlayer sync)
    {
        var now = DateTime.UtcNow;
        foreach (var (group, sid) in new[] { (Daily, DailyWindowSid), (Weekly, WeeklyWindowSid) })
        {
            uint window = (uint)Window(group, now);
            if (Player.Attributes.GetValue(PointGid, sid) == window) continue;
            foreach (var task in GameData.AchievementData.Values.Where(t => t.Group == group)) Write(Gid, task.ID, 0, sync);
            if (group == Daily)
            {
                Write(PointGid, PointSid, 0, sync);
                for (uint s = RewardSid; s <= AttrIds.Achievement.DailyRewardEndSid; ++s) Write(PointGid, s, 0, sync);
            }
            Write(PointGid, sid, window, sync);
        }
    }

    private uint Evaluate(AchievementExcel t, IReadOnlyCollection<AchievementExcel> tasks)
    {
        var inventory = Player.InventoryManager.InventoryData;
        var cards = Player.CharacterManager.CharacterData.Characters;
        if (t.Group == ChapterReward) return (uint)tasks.Count(x => x.Group == BranchLine && x.Chapter == t.Chapter && Claimed(x));
        int genre = t.ParamAt(0), value = t.ParamAt(1);
        return t.Type switch
        {
            1 => Player.Data.Level,
            3 => genre switch { 2 => (uint)inventory.Weapons.Count, 3 => (uint)inventory.SupportCards.Count, _ => 0 },
            26 => (uint)cards.Count,
            30 => (uint)cards.Count(c => c.Level >= genre),
            31 => (uint)inventory.Weapons.Values.Count(w => w.Level >= genre),
            46 => (uint)inventory.SupportCards.Values.Count(s => s.Level >= genre),
            42 => (uint)tasks.Count(x => x.Group is not (Daily or Weekly) && Claimed(x)),
            49 => genre switch
            {
                1 or 0 => (uint)cards.Count(c => GameData.CardData.Values.Any(d => d.Color == value && GameResourceTemplateId.FromGdpl(d.Genre, d.Detail, d.Particular, d.Level) == c.TemplateId)),
                3 => (uint)inventory.SupportCards.Values.Count(s => GameData.SupportCardData.Any(d => d.Color == value && d.TemplateId == s.TemplateId)),
                _ => 0,
            },
            65 => (uint)Player.BossManager.Total(),
            350 => Player.Attributes.GetValue(AttrIds.Quest.LevelPassGid, t.ParamLevel) > 0 ? 1u : 0,
            _ => Progress(t),   // counted, or not tracked
        };
    }

    /// <summary>A Neural Simulation clear: type 40 counts it, 48 {difficulty} (or any), 127 {difficulty, boss} when no operative went down.</summary>
    public void CountBoss(int difficulty, uint boss, int exits, NtfSyncPlayer sync)
    {
        foreach (var t in GameData.AchievementData.Values)
        {
            bool counts = t.Type switch
            {
                40 => true,
                48 => t.ParamAt(0) == 0 || t.ParamAt(0) == difficulty,
                127 => exits == 0 && t.ParamAt(0) == difficulty && (uint)t.ParamAt(1) == boss,
                _ => false,
            };
            if (counts) Write(Gid, t.ID, ((Progress(t) + 1) << 1) | (Claimed(t) ? 1u : 0), sync);
        }
    }

    /// <summary>Applies resets and brings every task up to date; changes go into <paramref name="sync"/>.</summary>
    public void Refresh(NtfSyncPlayer sync)
    {
        Reset(sync);
        var tasks = GameData.AchievementData.Values;
        foreach (var t in tasks)
        {
            uint claimed = Claimed(t) ? 1u : 0;
            Write(Gid, t.ID, (Evaluate(t, tasks) << 1) | claimed, sync);
        }
    }

    /// <summary>After a call: counts it if a counted one that changed something, then refreshes. Returns the sync to send (null: nothing to send).</summary>
    public NtfSyncPlayer? AfterCall(string api, NtfSyncPlayer? sync)
    {
        var result = sync ?? new NtfSyncPlayer();
        Reset(result);
        if (sync is { } done && (done.Items.Count > 0 || done.Custom.Count > 0 || done.Money.Count > 0) && CountedCalls.TryGetValue(api, out int type))
            foreach (var t in GameData.AchievementData.Values.Where(x => x.Type == type))
                Write(Gid, t.ID, ((Progress(t) + 1) << 1) | (Claimed(t) ? 1u : 0), result);
        Refresh(result);
        bool any = result.Custom.Count > 0;
        if (any) DatabaseHelper.SaveDatabaseType(Player.Data);
        return any || sync != null ? result : null;
    }

    /// <summary>Claims finished tasks and reached daily-point milestones (1-based), and returns the rewards.</summary>
    public async ValueTask<(JsonArray Rewards, NtfSyncPlayer Sync)> ClaimAsync(IEnumerable<uint> ids, IEnumerable<int> milestones)
    {
        var sync = new NtfSyncPlayer();
        Refresh(sync);
        var rewards = new JsonArray();
        async Task Grant(IReadOnlyList<IReadOnlyList<uint>> rows)
        {
            foreach (var reward in await Player.RewardManager.GrantConfiguredRewardsAsync(rows, sync))
                rewards.Add(reward?.DeepClone());
        }
        foreach (uint id in ids.Distinct())
        {
            if (!GameData.AchievementData.TryGetValue(id, out var t) || Claimed(t)) continue;
            uint need = t.Group == ChapterReward ? (uint)GameData.AchievementData.Values.Count(x => x.Group == BranchLine && x.Chapter == t.Chapter) : t.CheckValue;
            if (Progress(t) < need) continue;
            if (t.PreId != 0 && GameData.AchievementData.TryGetValue(t.PreId, out var pre) && !Claimed(pre)) continue;
            Write(Gid, t.ID, Player.Attributes.GetValue(Gid, t.ID) | 1, sync);
            if (t.Group == Daily && t.Point > 0) Write(PointGid, PointSid, Player.Attributes.GetValue(PointGid, PointSid) + (uint)t.Point, sync);
            await Grant(t.Rewards);
        }
        var points = GameData.AchievementPointRewardData.OrderBy(p => p.Point).ToList();
        foreach (int index in milestones.Distinct())
        {
            if (index < 1 || index > points.Count) continue;
            uint sid = RewardSid + (uint)index - 1;
            if (sid > AttrIds.Achievement.DailyRewardEndSid || Player.Attributes.GetValue(PointGid, sid) != 0
                || Player.Attributes.GetValue(PointGid, PointSid) < points[index - 1].Point) continue;
            Write(PointGid, sid, 1, sync);
            await Grant(points[index - 1].Rewards);
        }
        Refresh(sync);
        DatabaseHelper.SaveDatabaseType(Player.Data);
        return (rewards, sync);
    }
}
