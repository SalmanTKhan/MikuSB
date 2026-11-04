using System.Globalization;
using System.Text.Json.Nodes;
using MikuSB.Data;
using MikuSB.Data.Excel;
using MikuSB.Database;
using MikuSB.GameServer.Game.Player;
using MikuSB.Proto;

namespace MikuSB.GameServer.Game.Boss;

/// <summary>
/// Neural Simulation, in the string attributes the retail client reads (Boss.lua, gid 50): sid 0 the open period, 1-100 milestones claimed, 101 the highest difficulty
/// cleared; per boss from 10 * id + 100: +1..+3 the lineup as json {role}, +4 best score, +5 time, +6 wins, +7 tries, +8 (ours) the best's difficulty. The retail schedule
/// ended in 2023, so periods take turns weekly from Monday 04:00 UTC+8.
/// </summary>
public sealed class BossManager(PlayerInstance player) : BasePlayerManager(player)
{
    private const uint Gid = 50, PeriodSid = 0, DifficultySid = 101;
    private sealed record Session(uint Boss, int Seed, int Team, int Difficulty, List<uint> Entries);
    private Session? _session;

    private static uint Sid(uint boss, uint offset) => 10 * boss + 100 + offset;
    private int Number(uint sid) => int.TryParse(Player.Attributes.GetStringValue(Gid, sid), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

    private void Write(uint sid, string value, NtfSyncPlayer sync)
    {
        var attr = Player.Attributes.SetString(Gid, sid, value);
        Player.Attributes.SyncTo(sync, attr);
    }

    private void Write(uint sid, int value, NtfSyncPlayer sync) => Write(sid, value.ToString(CultureInfo.InvariantCulture), sync);

    private static BossPeriodExcel? Current()
    {
        var periods = GameData.BossPeriodData.Values.OrderBy(p => p.ID).Where(p => p.Bosses.Any(GameData.BossStageData.ContainsKey)).ToList();
        if (periods.Count == 0) return null;
        long days = (long)(DateTime.UtcNow + TimeSpan.FromHours(4) - DateTime.UnixEpoch).TotalDays;
        return periods[(int)((days + 3) / 7 % periods.Count)];
    }

    /// <summary>A new period clears its records and milestones; the difficulty reached stays.</summary>
    private BossPeriodExcel? Sync(NtfSyncPlayer sync)
    {
        var period = Current();
        if (period == null || Number(PeriodSid) == period.ID) return period;
        foreach (var attr in Player.Attributes.AllStrings.Where(a => a.Gid == Gid && a.Sid != DifficultySid && a.Sid != PeriodSid && a.Val != "0" && a.Val != "").ToList())
            Write(attr.Sid, attr.Sid is > 100 and var s && (s - 100) % 10 is 1 or 2 or 3 ? "" : "0", sync);
        Write(PeriodSid, (int)period.ID, sync);
        DatabaseHelper.SaveDatabaseType(Player.Data);
        return period;
    }

    public (uint Id, NtfSyncPlayer Sync) Open()
    {
        var sync = new NtfSyncPlayer();
        return (Sync(sync)?.ID ?? 0, sync);
    }

    private IEnumerable<uint> Team(int team) =>
        Player.LineupManager.LineupData.LineupInfo.TryGetValue(team, out var lineup) ? new[] { lineup.Member1, lineup.Member2, lineup.Member3 }.Where(m => m != 0) : [];

    private IEnumerable<uint> Recorded(uint boss) => new uint[] { 1, 2, 3 }
        .Select(i => Player.Attributes.GetStringValue(Gid, Sid(boss, i)))
        .Select(json => string.IsNullOrEmpty(json) ? 0u : JsonNode.Parse(json)?["role"]?.GetValue<uint>() ?? 0u).Where(r => r != 0);

    public (string? Error, int Seed, NtfSyncPlayer Sync) Enter(uint boss, int team, int difficulty, List<uint> entries)
    {
        var sync = new NtfSyncPlayer();
        var period = Sync(sync);
        if (period == null || !period.Bosses.Contains(boss) || !GameData.BossStageData.TryGetValue(boss, out var stage)) return ("error.BadParam", 0, sync);
        if (difficulty < 1 || difficulty > stage.Levels.Count || difficulty > Number(DifficultySid) + 1) return ("error.BadParam", 0, sync);
        foreach (uint id in entries)
            if (!GameData.BossEntryData.TryGetValue(id, out var entry) || entry.DiffLimit > difficulty || entry.Mutex.Any(entries.Contains)) return ("error.BadParam", 0, sync);
        var members = Team(team).ToList();
        if (members.Count == 0) return ("error.BadParam", 0, sync);
        foreach (uint other in period.Bosses.Where(b => b != boss))
            if (Recorded(other).Any(members.Contains)) return ("bossentries.lock1", 0, sync);
        int seed = Random.Shared.Next(1, 1_000_000_000);
        _session = new Session(boss, seed, team, difficulty, [.. entries.Distinct()]);
        Write(Sid(boss, 7), Number(Sid(boss, 7)) + 1, sync);
        DatabaseHelper.SaveDatabaseType(Player.Data);
        return (null, seed, sync);
    }

    public void Fail() => _session = null;

    public (string? Error, int Score, int Best, NtfSyncPlayer Sync) Settle(uint boss, int seed, int exits)
    {
        var sync = new NtfSyncPlayer();
        if (_session is not { } session || session.Boss != boss || session.Seed != seed || !GameData.BossStageData.TryGetValue(boss, out var stage))
            return ("error.BadParam", 0, 0, sync);
        _session = null;
        var level = stage.Levels[session.Difficulty - 1];
        double sum = session.Entries.Sum(e => GameData.BossEntryData.GetValueOrDefault(e)?.Score ?? 0) + level[3];
        int score = (int)Math.Floor(sum * level[2] + 0.5);
        int best = Number(Sid(boss, 4));
        Write(Sid(boss, 6), Number(Sid(boss, 6)) + 1, sync);
        if (score > best)
        {
            best = score;
            Write(Sid(boss, 4), score, sync);
            Write(Sid(boss, 8), session.Difficulty, sync);
            var members = Team(session.Team).ToList();
            for (int i = 0; i < 3; ++i)
                Write(Sid(boss, (uint)i + 1), i < members.Count ? new JsonObject { ["role"] = members[i] }.ToJsonString() : "", sync);
        }
        if (session.Difficulty > Number(DifficultySid)) Write(DifficultySid, session.Difficulty, sync);
        Player.AchievementManager.CountBoss(session.Difficulty, stage.BossID, exits, sync);
        DatabaseHelper.SaveDatabaseType(Player.Data);
        return (null, score, best, sync);
    }

    public NtfSyncPlayer Reset(uint boss)
    {
        var sync = new NtfSyncPlayer();
        Sync(sync);
        foreach (uint i in new uint[] { 1, 2, 3 }) Write(Sid(boss, i), "", sync);
        foreach (uint i in new uint[] { 4, 5, 8 }) Write(Sid(boss, i), 0, sync);
        DatabaseHelper.SaveDatabaseType(Player.Data);
        return sync;
    }

    /// <summary>The period's total score: the bosses' bests.</summary>
    public int Total() => Current()?.Bosses.Sum(b => Number(Sid(b, 4))) ?? 0;

    /// <summary>Claims one milestone, or with 0 every one reached.</summary>
    public async ValueTask<(JsonArray Rewards, NtfSyncPlayer Sync)> ClaimAsync(uint mileage)
    {
        var sync = new NtfSyncPlayer();
        Sync(sync);
        var rewards = new JsonArray();
        int total = Total();
        foreach (var award in GameData.BossAwardData.Values.OrderBy(a => a.ID).Where(a => (mileage == 0 || a.ID == mileage) && total >= a.ScoreCount && Number(a.ID) == 0))
        {
            Write(award.ID, 1, sync);
            foreach (var reward in await Player.RewardManager.GrantConfiguredRewardsAsync(award.Rewards, sync)) rewards.Add(reward?.DeepClone());
        }
        DatabaseHelper.SaveDatabaseType(Player.Data);
        return (rewards, sync);
    }
}
