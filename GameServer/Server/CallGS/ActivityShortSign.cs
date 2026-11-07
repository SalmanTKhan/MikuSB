using MikuSB.Data;
using MikuSB.Database;
using MikuSB.GameServer.Game.Activity;
using MikuSB.GameServer.Game.Dlc;
using MikuSB.GameServer.Game.Player;
using MikuSB.Proto;
using MikuSB.Util;
using System.Text.Json.Nodes;

namespace MikuSB.GameServer.Server.CallGS;

/// <summary>Short sign-in (signshort.json): one claim fills the 7 day card, and the counter stops any repeat.</summary>
public static class ActivityShortSign
{
    public static async Task<CallGSResult> GrantShortSignAsync(
        PlayerInstance player,
        uint activityId,
        JsonObject response)
    {
        var sync = new NtfSyncPlayer();
        var rewards = new JsonArray();

        var days = new SortedDictionary<uint, Newtonsoft.Json.Linq.JToken?>();
        foreach (var pair in GameData.SignShortData)
        {
            if (pair.Key.ActivityId != activityId)
                continue;

            days[pair.Key.DayId] = pair.Value.SignReward;
        }

        if (days.Count == 0)
        {
            // Not a short-sign card, or the table did not load: still answer on the ShortSign callback
            // so the client's spinner closes instead of staying up forever.
            Logger.GetByClassName().Warn(
                $"Activity {activityId} has no signshort row; answering ShortSign without a reward.");
            response["tbRewards"] = rewards;
            return CallGSResult.Ok(response.ToJsonString(), sync, "ShortSign");
        }

        var maxDay = days.Keys.Max();
        var tag = player.Attributes.GetValue(ActivitySignService.GroupId,
            ActivitySignService.SignedDaySid(activityId));
        var status = player.Attributes.GetValue(ActivitySignService.GroupId,
            ActivitySignService.TodayStatusSid(activityId));

        if (tag >= maxDay)
        {
            // Card already full. Keep the status at "shown" so the widget stops auto-sending and stay
            // silent about rewards - this is the path a reopened window / replicated era hits.
            ActivitySignService.SetAttr(player, ActivitySignService.TodayStatusSid(activityId),
                ActivitySignService.StatusSigned, sync);
            response["tbRewards"] = rewards;

            Logger.GetByClassName().Info(
                $"Short sign activity {activityId} already complete for uid={player.Uid} " +
                $"(day {tag}/{maxDay}); no reward.");
            return CallGSResult.Ok(response.ToJsonString(), sync, "ShortSign");
        }

        var startDay = status is ActivitySignService.StatusPending or 3 ? tag + 1 : tag;
        var fillsCard = ConfigManager.Config.ServerOption.ShortSignFillsCard;
        var lastSign = player.Attributes.GetValue(ActivitySignService.GroupId, ActivitySignService.LastSignDateSid(activityId));
        if (!fillsCard && lastSign == ActivitySignService.TodayStamp())
        {
            ActivitySignService.SetAttr(player, ActivitySignService.TodayStatusSid(activityId),
                ActivitySignService.StatusSigned, sync);
            response["tbRewards"] = rewards;
            return CallGSResult.Ok(response.ToJsonString(), sync, "ShortSign");
        }

        // Refresh seeds the counter at 1 before anything is paid, so day 1 is still owed on the first claim.
        if (startDay == 0 || player.Attributes.GetValue(ActivitySignService.GroupId, ActivitySignService.LastSignDateSid(activityId)) == 0)
            startDay = 1;
        if (startDay > maxDay)
            startDay = maxDay;

        foreach (var (day, rewardJson) in days)
        {
            if (day < startDay || (!fillsCard && day > startDay))
                continue;

            var rows = DlcRuntimeOverrides.ReadRewardRows(rewardJson);
            var granted = await player.RewardManager.GrantConfiguredRewardsAsync(rows, sync);
            foreach (var row in granted)
                rewards.Add(row?.DeepClone());
        }

        var counter = fillsCard ? maxDay : startDay;
        ActivitySignService.SetAttr(player, ActivitySignService.SignedDaySid(activityId), counter, sync);
        ActivitySignService.SetAttr(player, ActivitySignService.TodayStatusSid(activityId),
            ActivitySignService.StatusSigned, sync);
        ActivitySignService.SetAttr(player, ActivitySignService.LastSignDateSid(activityId),
            ActivitySignService.TodayStamp(), sync);

        response["tbRewards"] = rewards;

        Logger.GetByClassName().Info(
            $"Short sign activity {activityId} for uid={player.Uid}: granted day {startDay}..{counter} " +
            $"({rewards.Count} reward row(s)), counter now {counter}.");

        DatabaseHelper.SaveDatabaseType(player.Data);
        DatabaseHelper.SaveDatabaseType(player.InventoryManager.InventoryData);
        DatabaseHelper.SaveDatabaseType(player.CharacterManager.CharacterData);

        return CallGSResult.Ok(response.ToJsonString(), sync, "ShortSign");
    }
}
