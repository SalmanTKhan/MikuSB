using MikuSB.Data;
using MikuSB.Data.Excel;
using MikuSB.GameServer.Game.Player;
using MikuSB.Proto;
using System.Globalization;
using System.Text.Json.Nodes;

namespace MikuSB.GameServer.Server.CallGS.Handlers.BattlePass;

[CallGSApi("BattlePassLogic_ClientRefresh")]
public class BattlePassLogic_ClientRefresh : CallGSHandler
{
    private const uint GroupId = AttrIds.BattlePass.Gid;
    private const uint CurIdSid = AttrIds.BattlePass.CurrentIdSid;
    // The original server answered every refresh in the 2026-10-25 capture with an empty list (7 of 7);
    // the current battle pass reaches the client through the attribute sync only.
    private const string RetailResponse = "[]";

    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var sync = new NtfSyncPlayer();
        MikuSB.GameServer.Game.BattlePass.BattlePassService.Refresh(context.Connection.Player!, sync);
        return Task.FromResult(CallGSResult.Ok(RetailResponse, sync));
    }

    private static BattlePassTimeExcel? ResolveCurrent(IEnumerable<BattlePassTimeExcel> configs, DateTime now)
    {
        var parsed = configs
            .Select(x => new
            {
                Config = x,
                Start = ParseConfigTime(x.StartTime),
                End = ParseConfigTime(x.EndTime)
            })
            .Where(x => x.Start.HasValue && x.End.HasValue)
            .OrderBy(x => x.Start)
            .ToList();

        var current = parsed.FirstOrDefault(x => x.Start <= now && now < x.End);
        if (current != null)
            return current.Config;

        var latestStarted = parsed.LastOrDefault(x => x.Start <= now && x.End > x.Start);
        return latestStarted?.Config;
    }

    private static DateTime? ParseConfigTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Trim().Trim('[', ']');
        if (normalized.Length != 12)
            return null;

        return DateTime.TryParseExact(
            normalized,
            "yyyyMMddHHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : null;
    }

    private static long ToUnixSeconds(DateTime? value)
    {
        return value.HasValue ? new DateTimeOffset(value.Value).ToUnixTimeSeconds() : 0L;
    }

    private static void SetAttr(PlayerInstance player, uint sid, uint value, NtfSyncPlayer sync)
    {
        var attr = player.Attributes.GetOrCreate(GroupId, sid);
        if (attr.Val != value)
        {
            attr.Val = value;
            player.Attributes.SyncTo(sync, attr);
        }
    }
}
