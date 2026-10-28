using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MikuSB.Data;
using MikuSB.Database;
using MikuSB.Database.Player;
using MikuSB.GameServer.Game.Player;
using MikuSB.GameServer.Game.Quest;
using MikuSB.GameServer.Server.CallGS.Handlers.Chapter;
using MikuSB.Proto;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.CallGS;

// Capture 2026-10-25: callback names/shapes are retail observations.
// Eligibility is evaluated from local state/resources, never from captured player data.
public static class CapturedGameplay
{
    private static readonly Logger Logger = new("CapturedGameplay");
    public static readonly TimeSpan ConfigTimeOffset = TimeSpan.FromHours(8);
    private static readonly ConcurrentDictionary<string, JsonArray> Resources = new();
    private static readonly ConditionalWeakTable<PlayerInstance, SemaphoreSlim> Gates = new();

    public static async Task<CallGSResult> HandleAsync(CallGSContext context, string api, string param)
    {
        var player = context.Player;
        var gate = Gates.GetValue(player, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var root = string.IsNullOrWhiteSpace(param) ? null : JsonNode.Parse(param);
            if (api is "Launch_OnCloseLoadingLog" or "FavMailLog" or "Online_ClientLog" or "DownloadMultMediaLog")
                return CallGSResult.NoResponse(); // Empty RspCallGS is sent separately by the router.
            if (root is not JsonObject obj) return CallGSResult.Error("error.BadParam");
            var state = DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(player.Uid);
            switch (api)
            {
                case "Chapter_NewPrologueEnterLevel":
                {
                    var id = UInt(obj, "nID"); var team = UInt(obj, "nTeamID");
                    if (id == 0 || team == 0 || !player.QuestManager.CanEnterLevel(QuestLevelType.Chapter, id))
                        return CallGSResult.Error("error.BadParam");
                    var seed = (uint)Random.Shared.Next(1, 65536);
                    player.BeginLevelSession(QuestLevelType.Chapter, id, seed, team);
                    return CallGSResult.Ok(new JsonObject { ["nSeed"] = seed });
                }
                case "Chapter_NewPrologueSettlement":
                {
                    var id = UInt(obj, "nID");
                    if (!player.IsActiveChapterLevel(id)) return CallGSResult.Error("error.BadParam");
                    // Direct prologue completion is a checkpoint, not a reward claim.
                    var attr = player.Attributes.Set(AttrIds.Quest.LevelPassGid, id,
                        Math.Max(1, player.Attributes.GetValue(AttrIds.Quest.LevelPassGid, id)));
                    var sync = new NtfSyncPlayer(); player.Attributes.SyncTo(sync, attr);
                    DatabaseHelper.SaveDatabaseType(player.Data);
                    return CallGSResult.Ok(new JsonObject { ["nID"] = id,
                        ["bWaitServer"] = obj["bWaitServer"]?.DeepClone() ?? false }, sync);
                }
                case "GuideLogic_WriteGuideLog":
                {
                    var log = obj["logstr"]?.GetValue<string>();
                    if (log == null || log.Length > 256) return CallGSResult.Error("error.BadParam");
                    state.Values["guide:last"] = log;
                    DatabaseHelper.SaveDatabaseType(state);
                    return CallGSResult.Ok(new JsonObject { ["logstr"] = log });
                }
                case "GuideLogic_SetBeginner":
                {
                    var type = UInt(obj, "nType");
                    if (type is not (1 or 2)) return CallGSResult.Error("error.BadParam");
                    var sync = new NtfSyncPlayer();
                    // Retail ExtraSync custom 12255233 = (187 << 16) | 1.
                    player.Attributes.SyncTo(sync, player.Attributes.Set(AttrIds.Guide.BeginnerGroupId, 1, type));
                    DatabaseHelper.SaveDatabaseType(player.Data);
                    return CallGSResult.Ok("null", sync);
                }
                case "PlayerSetting_SetBirthday":
                {
                    // Retail: {nDay:9,nMouth:1} -> callback null with custom 9895937 = (151 << 16) | 1 set to the
                    // current year. Retail also sent month/day inside NtfSyncPlayer.show_attrs; those indices are
                    // unverified, so month/day are kept in local preservation state only.
                    var month = UInt(obj, "nMouth"); var day = UInt(obj, "nDay");
                    if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2000, (int)month))
                        return CallGSResult.Error("error.BadParam");
                    var year = (uint)DateTimeOffset.UtcNow.ToOffset(ConfigTimeOffset).Year;
                    var sync = new NtfSyncPlayer();
                    player.Attributes.SyncTo(sync, player.Attributes.Set(AttrIds.Player.AccountBirthdayGroupId, AttrIds.Player.AccountBirthdaySid, year));
                    state.Values["birthday"] = $"{month}-{day}";
                    DatabaseHelper.SaveDatabaseType(player.Data);
                    DatabaseHelper.SaveDatabaseType(state);
                    return CallGSResult.Ok("null", sync);
                }
                case "OnlineAssaultOpsBattlePass_ChangeBP":
                {
                    var id = UInt(obj, "nId");
                    if (id != 1) return CallGSResult.Error("error.BadParam");
                    var sync = new NtfSyncPlayer();
                    // Retail ExtraSync custom 12058625 = (184 << 16) | 1.
                    player.Attributes.SyncTo(sync, player.Attributes.Set(AttrIds.Online.AssaultOpsBattlePassGroupId, 1, id));
                    DatabaseHelper.SaveDatabaseType(player.Data);
                    return CallGSResult.Ok(new JsonObject { ["nId"] = id }, sync);
                }
                case "LimitedTime_CheckLimitedRoleGet":
                    // This capture has no reward callback for the check. Do not grant
                    // client-selected GDPLs without a verified entitlement resolver.
                    return CallGSResult.NoResponse();
                case "Activity_Flag":
                {
                    var id = UInt(obj, "nId"); var type = UInt(obj, "nType");
                    if (!Rows("activity/activities.json").Any(r => UInt(r, "Id") == id))
                        return CallGSResult.Error("error.BadParam");
                    state.Values[$"activity:{id}:flag"] = type.ToString();
                    DatabaseHelper.SaveDatabaseType(state);
                    return CallGSResult.Ok(new JsonObject { ["nId"] = id, ["nType"] = type }, api: "ActivityQuest_Flag");
                }
                case "Activity_GetAward":
                case "Activity_QuickQuestAward":
                    return await ClaimActivity(player, state, obj, api == "Activity_QuickQuestAward");
                default: return CallGSResult.Error("ui.TxtNotOpen");
            }
        }
        finally { gate.Release(); }
    }

    public static uint UInt(JsonNode? node, string field) =>
        node?[field] is JsonValue value && value.TryGetValue<uint>(out var result) ? result : 0;

    public static IEnumerable<JsonNode> Rows(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(ConfigManager.Config.Path.ResourcePath, relative));
        return Resources.GetOrAdd(path, p => JsonNode.Parse(File.ReadAllText(p)) as JsonArray
            ?? throw new InvalidDataException("Expected resource array")) .OfType<JsonNode>();
    }

    private static bool Passed(PlayerInstance player, uint id) =>
        player.Attributes.GetValue(AttrIds.Quest.LevelPassGid, id) > 0;

    /// <summary>
    /// Evaluates an activity's StartTime/EndTime window and Condition list. Condition is read as a list of
    /// alternatives, each a list of clauses that must all hold (e.g. new prologue 50174 or legacy 10104).
    /// Only clause types 1 (player level) and 2 (level passed) are resolved; every other clause type and the
    /// dictionary-shaped variants fail closed. Times are yyyyMMddHHmm in UTC+8 (verified against retail shop open times).
    /// </summary>
    public static bool ActivityOpen(PlayerInstance player, JsonNode activity, DateTimeOffset now, out string? reason)
    {
        if (TryReadTime(activity["StartTime"], out var start) && now < start) { reason = "not started"; return false; }
        if (TryReadTime(activity["EndTime"], out var end) && now >= end) { reason = "ended"; return false; }
        switch (activity["Condition"])
        {
            case null:
            case JsonValue v when v.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text):
                reason = null;
                return true;
            case JsonArray alternatives:
                foreach (var alternative in alternatives)
                {
                    if (alternative is JsonArray clauses && clauses.Count > 0 && clauses.All(c => ClauseHolds(player, c)))
                    {
                        reason = null;
                        return true;
                    }
                }
                reason = $"condition unmet or unsupported {alternatives.ToJsonString()}";
                return false;
            default:
                reason = $"unsupported condition shape {activity["Condition"]!.ToJsonString()}";
                return false;
        }
    }

    private static bool ClauseHolds(PlayerInstance player, JsonNode? clause)
    {
        if (clause is not JsonArray { Count: 2 } c || c[0] is not JsonValue t || !t.TryGetValue<uint>(out var type)
            || c[1] is not JsonValue a || !a.TryGetValue<uint>(out var arg))
            return false;
        return type switch
        {
            1 => player.Data.Level >= arg,
            2 => Passed(player, arg),
            _ => false
        };
    }

    /// <summary>Reads a config time like "[202109010000]". Config times are UTC+8: retail ShopLogic_GetOpenTime
    /// returned 1630425600 for shop_tab Begin 202109010000.</summary>
    public static bool TryReadTime(JsonNode? node, out DateTimeOffset time)
    {
        time = default;
        var text = node switch
        {
            JsonArray { Count: 1 } a => a[0]?.ToString(),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Trim('[', ']'),
            JsonValue v => v.ToString(),
            _ => null
        };
        if (text == null || !DateTime.TryParseExact(text, "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
            return false;
        time = new DateTimeOffset(local, ConfigTimeOffset);
        return true;
    }

    public static bool QuestEligible(PlayerInstance player, JsonNode quest)
    {
        var target = UInt(quest, "CheckValue");
        return UInt(quest, "Type") switch
        {
            187 => player.Data.Level >= target,
            350 when quest["Param"] is JsonArray { Count: >= 2 } p && p[1] is JsonArray levels =>
                levels.OfType<JsonValue>().Count(v => v.TryGetValue<uint>(out var id) && Passed(player, id)) >= target,
            _ => false // House-specific counters need an authoritative resolver; never trust claim input.
        };
    }

    private static async Task<CallGSResult> ClaimActivity(PlayerInstance player, PreservationProgressData state,
        JsonObject request, bool quick)
    {
        var id = UInt(request, "nId");
        var activity = Rows("activity/activities.json").FirstOrDefault(r => UInt(r, "Id") == id);
        if (activity == null) return CallGSResult.Error("error.BadParam");
        if (!ActivityOpen(player, activity, DateTimeOffset.UtcNow, out var closed))
        {
            Logger.Warn($"Activity claim rejected, activity closed. uid={player.Data.Uid} activity={id} reason={closed}");
            return CallGSResult.Error("ui.TxtNotOpen");
        }
        var claims = new List<(string Key, JsonArray Rewards)>();
        string callback;
        JsonObject response;
        if (activity["Class"]?.GetValue<string>() == "signin_monthly" && !quick)
        {
            var key = $"sign:{id}:{DateTimeOffset.UtcNow:yyyy-MM-dd}";
            // SignDay is cumulative login day in retail, not a client-supplied reward index.
            var signed = state.Claims.Count(k => k.StartsWith($"sign:{id}:{DateTimeOffset.UtcNow:yyyy-MM}-", StringComparison.Ordinal));
            var row = Rows("activity/sign/signday.json").FirstOrDefault(r => UInt(r, "ActivityId") == id && UInt(r, "DayId") == signed + 1);
            if (row?["SignReward"] is not JsonArray rewards) return CallGSResult.Error("ui.TxtNotOpen");
            claims.Add((key, rewards)); callback = "DaySign";
            response = new JsonObject { ["nId"] = id, ["nType"] = UInt(request, "nType") };
        }
        else if (activity["Class"]?.GetValue<string>() == "activity_quest")
        {
            var ids = quick ? request["tbIdList"] as JsonArray : new JsonArray(UInt(request, "nQuestId"));
            if (ids == null || ids.Count is < 1 or > 100) return CallGSResult.Error("error.BadParam");
            var unique = new HashSet<uint>();
            foreach (var value in ids)
            {
                if (value is not JsonValue number || !number.TryGetValue<uint>(out var questId) || !unique.Add(questId))
                    return CallGSResult.Error("error.BadParam");
                var allowed = activity["Normal"] is JsonArray normal && normal.Any(v => v is JsonValue n && n.TryGetValue<uint>(out var q) && q == questId);
                var quest = Rows("achievement/activity_quest.json").FirstOrDefault(r => UInt(r, "ID") == questId);
                if (!allowed || quest == null || !QuestEligible(player, quest) || quest["Rewards"] is not JsonArray rewards)
                    return CallGSResult.Error("error.BadParam");
                claims.Add(($"quest:{id}:{questId}", rewards));
            }
            callback = quick ? "Activity_QuickQuestAward" : "ActivityQuest_GetReward";
            response = new JsonObject { ["nId"] = id };
            if (quick) response["tbIdList"] = ids.DeepClone();
            else response["nQuestId"] = UInt(request, "nQuestId");
        }
        else return CallGSResult.Error("ui.TxtNotOpen");
        if (claims.Any(c => state.Claims.Contains(c.Key)))
            return CallGSResult.Error("error.BadParam");
        var rows = new List<IReadOnlyList<uint>>();
        foreach (var (_, rewards) in claims)
            foreach (var reward in rewards)
            {
                if (reward is not JsonArray row || row.Count != 5 || row.Any(n => n is not JsonValue v || !v.TryGetValue<uint>(out _)))
                    return CallGSResult.Error("error.BadParam");
                rows.Add(row.Select(n => n!.GetValue<uint>()).ToArray());
            }
        if (!player.RewardManager.CanGrantConfiguredRewards(rows, out var reason))
        {
            Logger.Warn($"Activity claim rejected before grant. uid={player.Data.Uid} activity={id} reason={reason}");
            return CallGSResult.Error("error.BadParam");
        }
        var sync = new NtfSyncPlayer();
        var (granted, complete) = await player.RewardManager.GrantConfiguredRewardsStrictAsync(rows, sync);
        response["tbRewards"] = granted;
        // Prevalidation covers template lookups; a failure past it is an inventory-side defect. The granted
        // part is persisted with the claim so it cannot be farmed by retrying.
        if (!complete)
            Logger.Error($"Activity claim granted partially after prevalidation. uid={player.Data.Uid} activity={id} granted={granted.ToJsonString()}");
        foreach (var claim in claims) state.Claims.Add(claim.Key);
        DatabaseHelper.SaveDatabaseType(player.Data);
        DatabaseHelper.SaveDatabaseType(player.InventoryManager.InventoryData);
        DatabaseHelper.SaveDatabaseType(player.CharacterManager.CharacterData);
        DatabaseHelper.SaveDatabaseType(state);
        return CallGSResult.Ok(response, sync, callback);
    }
}
