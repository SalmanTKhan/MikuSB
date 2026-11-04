using System.Globalization;
using System.Text.Json.Nodes;
using MikuSB.Data;
using MikuSB.Data.Excel;
using MikuSB.Database;
using MikuSB.Database.Player;
using MikuSB.GameServer.Game.Player;
using MikuSB.GameServer.Game.Shop;
using MikuSB.GameServer.Server.CallGS;
using MikuSB.Proto;

namespace MikuSB.GameServer.Game.BattlePass;

/// <summary>
/// The pass's rules (BattlePass.lua), attributes gid 25: 1 season, 2 tier (0 / 1 / 2), 3 XP, 4 XP from daily tasks, 5 from weekly tasks,
/// 11 first opened, 100 / 200 the last free / premium level claimed. Level = XP / ExpStep. Missions are activity_quest rows in attribute
/// gid 7 (bit 0 claimed); their XP rewards are items {4,1,4,1..3} whose count is the XP. The tier items, weekly counter and
/// tables are the game's; daily and weekly XP resets at 04:00 UTC+8 are this server's reading of the script's comments.
/// </summary>
public static class BattlePassService
{
    public const uint Gid = AttrIds.BattlePass.Gid;
    public const uint Season = AttrIds.BattlePass.CurrentIdSid, Tier = AttrIds.BattlePass.StatusSid, Exp = 3, DailyExp = 4, WeeklyExp = 5, FirstOpen = 11, NormalClaimed = 100, AdvanceClaimed = 200;
    private const uint QuestGid = AttrIds.Achievement.QuestGroupId;

    /// <summary>The running season, else the latest started (the resources end before today).</summary>
    public static BattlePassTimeExcel? Current(DateTime now)
    {
        var seasons = GameData.BattlePassTimeData.Values
            .Select(x => (Config: x, Start: Parse(x.StartTime), End: Parse(x.EndTime)))
            .Where(x => x.Start != null && x.End != null).OrderBy(x => x.Start).ToList();
        var running = seasons.FirstOrDefault(x => x.Start <= now && now < x.End);
        return running.Config ?? seasons.LastOrDefault(x => x.Start <= now).Config;
    }

    private static DateTime? Parse(string? raw) =>
        DateTime.TryParseExact((raw ?? "").Trim().Trim('[', ']'), "yyyyMMddHHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    private static void Write(PlayerInstance player, uint sid, uint value, NtfSyncPlayer sync)
    {
        var attr = player.Attributes.GetOrCreate(Gid, sid);
        if (attr.Val == value) return;
        attr.Val = value;
        player.Attributes.SyncTo(sync, attr);
    }

    /// <summary>Puts the current season and today's windows in the attributes: a new season starts from nothing; daily / weekly XP counters reset.</summary>
    public static BattlePassTimeExcel? Refresh(PlayerInstance player, NtfSyncPlayer sync)
    {
        var season = Current(DateTime.Now);
        uint id = season?.Id ?? 0;
        var old = player.Attributes.GetValue(Gid, Season);
        if (old != id)
        {
            if (old != 0)
                foreach (uint sid in new[] { Tier, Exp, DailyExp, WeeklyExp, FirstOpen, NormalClaimed, AdvanceClaimed }) Write(player, sid, 0, sync);
            Write(player, Season, id, sync);
        }
        var state = DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(player.Uid);
        var now = DateTimeOffset.UtcNow;
        bool changed = false;
        void Window(string key, uint limit, uint sid)
        {
            var stamp = ShopService.Window(limit, now).ToString();
            if (state.Values.GetValueOrDefault(key) == stamp) return;
            state.Values[key] = stamp;
            changed = true;
            Write(player, sid, 0, sync);
            if (season != null) ResetQuests(player, sid == DailyExp ? Quests(season, "Daily") : Quests(season, "Weekly"), sync);
        }
        Window("bp:day", 1, DailyExp);
        Window("bp:week", 2, WeeklyExp);
        if (changed) DatabaseHelper.SaveDatabaseType(state);
        return season;
    }

    private static void ResetQuests(PlayerInstance player, IEnumerable<uint> ids, NtfSyncPlayer sync)
    {
        foreach (var id in ids)
            if (player.Attributes.Get(QuestGid, id) is { Val: not 0 } attr)
            {
                attr.Val = 0;
                player.Attributes.SyncTo(sync, attr);
            }
    }

    /// <summary>A season row's raw cell (Daily, Weekly, Normal, Money, NormalBox, AdvanceBox).</summary>
    private static JsonNode? Cell(uint season, string name) =>
        CapturedGameplay.Rows("battlepass/timelist.json").FirstOrDefault(r => CapturedGameplay.UInt(r, "ID") == season)?[name];

    private static uint[] Numbers(JsonNode? cell) =>
        cell is JsonArray a ? [.. a.OfType<JsonValue>().Select(v => v.TryGetValue<uint>(out var n) ? n : 0)] : [];

    private static uint[] Quests(BattlePassTimeExcel season, string kind) => Numbers(Cell(season.Id, kind));

    public static uint MaxLevel(uint season) =>
        CapturedGameplay.Rows("battlepass/levellist.json").Where(r => CapturedGameplay.UInt(r, "ID") == season).Select(r => CapturedGameplay.UInt(r, "Level")).DefaultIfEmpty(0u).Max();

    public static uint Level(PlayerInstance player, BattlePassTimeExcel season) =>
        season.ExpStep == 0 ? 0 : player.Attributes.GetValue(Gid, Exp) / season.ExpStep;

    /// <summary>BattlePassLogic_GetPassAward: every level reached and unclaimed, free and, with a pass, premium; a level past the top gives the season's box.</summary>
    public static async ValueTask<(JsonArray Awards, NtfSyncPlayer Sync)> ClaimLevels(PlayerInstance player)
    {
        var sync = new NtfSyncPlayer();
        var season = Refresh(player, sync);
        var rows = new List<IReadOnlyList<uint>>();
        if (season != null)
        {
            uint reached = Level(player, season), max = MaxLevel(season.Id);
            void Track(uint sid, string award, string box)
            {
                uint claimed = player.Attributes.GetValue(Gid, sid);
                if (reached <= claimed) return;
                foreach (var row in CapturedGameplay.Rows("battlepass/levellist.json").Where(r => CapturedGameplay.UInt(r, "ID") == season.Id))
                {
                    uint level = CapturedGameplay.UInt(row, "Level");
                    if (level > claimed && level <= reached && Numbers(row[award]) is { Length: >= 5 } cell) rows.Add(cell);
                }
                if (reached > max && Numbers(Cell(season.Id, box)) is { Length: >= 5 } b)
                    rows.Add([b[0], b[1], b[2], b[3], b[4] * (reached - Math.Max(claimed, max))]);
                Write(player, sid, reached, sync);
            }
            Track(NormalClaimed, "NormalAward", "NormalBox");
            if (player.Attributes.GetValue(Gid, Tier) > 0) Track(AdvanceClaimed, "AdvanceAward", "AdvanceBox");
        }
        var awards = await player.RewardManager.GrantConfiguredRewardsAsync(rows, sync);
        ShopService.Save(player);
        return (awards, sync);
    }

    /// <summary>
    /// BattlePassLogic_QuickGetReward {tbIdList: [[kind, quest]...]}: claims finished missions of this season. XP items become XP, up to the
    /// week's cap for daily and weekly missions; other rewards are granted. Only quest types this server can evaluate count as finished.
    /// </summary>
    public static async ValueTask<(JsonArray Awards, NtfSyncPlayer Sync)> ClaimMissions(PlayerInstance player, IEnumerable<uint> ids)
    {
        var sync = new NtfSyncPlayer();
        var awards = new JsonArray();
        var season = Refresh(player, sync);
        if (season == null) return (awards, sync);
        var daily = Quests(season, "Daily");
        var weekly = Quests(season, "Weekly");
        var allowed = daily.Concat(weekly).Concat(Quests(season, "Normal")).ToHashSet();
        foreach (var id in ids.Distinct())
        {
            if (!allowed.Contains(id)) continue;
            var quest = CapturedGameplay.Rows("achievement/activity_quest.json").FirstOrDefault(r => CapturedGameplay.UInt(r, "ID") == id);
            var attr = player.Attributes.GetOrCreate(QuestGid, id);
            if (quest == null || (attr.Val & 1) != 0 || !Finished(player, quest, id)) continue;
            attr.Val |= 1;
            player.Attributes.SyncTo(sync, attr);
            var other = new List<IReadOnlyList<uint>>();
            foreach (var row in (quest["Rewards"] as JsonArray ?? []).Select(Numbers).Where(r => r.Length >= 5))
            {
                if (row is [4, 1, 4, <= 3, ..])
                {
                    uint gained = AddExp(player, season, row[4], daily.Contains(id) ? DailyExp : weekly.Contains(id) ? WeeklyExp : 0, sync);
                    if (gained > 0) awards.Add(new JsonArray((int)row[0], (int)row[1], (int)row[2], (int)row[3], (int)gained));
                }
                else other.Add(row);
            }
            foreach (var granted in await player.RewardManager.GrantConfiguredRewardsAsync(other, sync)) awards.Add(granted?.DeepClone());
        }
        ShopService.Save(player);
        return (awards, sync);
    }

    private static bool Finished(PlayerInstance player, JsonNode quest, uint id)
    {
        // The login mission (5001) is the script's own: BattlePass.CheckLoginTask sets its progress on login.
        if (id == 5001) return true;
        return CapturedGameplay.QuestEligible(player, quest) || (player.Attributes.GetValue(QuestGid, id) >> 1) >= Math.Max(CapturedGameplay.UInt(quest, "CheckValue"), 1u);
    }

    private static uint AddExp(PlayerInstance player, BattlePassTimeExcel season, uint xp, uint counter, NtfSyncPlayer sync)
    {
        if (counter != 0)
        {
            uint week = player.Attributes.GetValue(Gid, WeeklyExp) + player.Attributes.GetValue(Gid, DailyExp);
            xp = season.MaxExPerWeek > week ? Math.Min(xp, season.MaxExPerWeek - week) : 0;
        }
        if (xp == 0) return 0;
        Write(player, Exp, player.Attributes.GetValue(Gid, Exp) + xp, sync);
        if (counter != 0) Write(player, counter, player.Attributes.GetValue(Gid, counter) + xp, sync);
        return xp;
    }

    /// <summary>BattlePassLogic_BuyLevel {nLevel}: Money per level (the season's {2,200}), needs a pass, up to the top level. The error key, or null.</summary>
    public static string? BuyLevels(PlayerInstance player, uint levels, NtfSyncPlayer sync)
    {
        var season = Refresh(player, sync);
        if (season == null || season.ExpStep == 0) return "tip.BattlePass_Level_Error";
        if (player.Attributes.GetValue(Gid, Tier) == 0) return "tip.BattlePass_NotGet_BP";
        if (Level(player, season) + levels > MaxLevel(season.Id)) return "tip.BattlePass_Level_Max";
        uint[] money = Numbers(Cell(season.Id, "Money"));
        if (money.Length != 2) return "tip.BattlePass_Level_Error";
        if (!ShopService.CanPay(player, [money], levels)) return "tip.gold_not_enough";
        ShopService.Pay(player, [money], levels, sync);
        Write(player, Exp, player.Attributes.GetValue(Gid, Exp) + levels * season.ExpStep, sync);
        ShopService.Save(player);
        return null;
    }

    public static void MarkOpened(PlayerInstance player, NtfSyncPlayer sync)
    {
        Write(player, FirstOpen, 1, sync);
        DatabaseHelper.SaveDatabaseType(player.Data);
    }
}
