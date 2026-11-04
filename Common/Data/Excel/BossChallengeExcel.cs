using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MikuSB.Data.Excel;

/// <summary>Neural Simulation (challenge/boss; the retail client's Challenge/Boss/Boss.lua).</summary>
[ResourceEntity("challenge/boss/boss.json")]
public class BossStageExcel : ExcelResource
{
    public uint ID { get; set; }
    public uint BossID { get; set; }
    [JsonProperty("BossLevel")] public JToken? LevelsRaw { get; set; }
    [JsonProperty("BossEntries")] public JToken? PoolsRaw { get; set; }

    /// <summary>Difficulty (1-based): {index, monster level, score multiplier, base score}.</summary>
    [JsonIgnore] public IReadOnlyList<double[]> Levels => LevelsRaw is JArray rows ? [.. rows.OfType<JArray>().Select(r => r.Select(v => v.Value<double>()).ToArray())] : [];
    [JsonIgnore] public IEnumerable<uint> Pools => BossExcelRead.Ints(PoolsRaw);

    public override uint GetId() => ID;
    public override void Loaded() => GameData.BossStageData[ID] = this;
}

[ResourceEntity("challenge/boss/boss_challenge.json")]
public class BossPeriodExcel : ExcelResource
{
    public uint ID { get; set; }
    [JsonExtensionData] public IDictionary<string, JToken>? Rest { get; set; }

    [JsonIgnore]
    public IReadOnlyList<uint> Bosses => [.. Enumerable.Range(1, 10)
        .Select(i => Rest != null && Rest.TryGetValue($"Boss{i}", out var v) && v.Type == JTokenType.Integer ? v.Value<uint>() : 0).Where(v => v != 0)];

    public override uint GetId() => ID;
    public override void Loaded() => GameData.BossPeriodData[ID] = this;
}

[ResourceEntity("challenge/boss/entries.json")]
public class BossEntryExcel : ExcelResource
{
    public uint ID { get; set; }
    [JsonProperty("Score")] public JToken? ScoreRaw { get; set; }
    [JsonProperty("DiffLimit")] public JToken? DiffLimitRaw { get; set; }
    [JsonProperty("tbMutex")] public JToken? MutexRaw { get; set; }

    [JsonIgnore] public int Score => AchievementExcel.ReadInt(ScoreRaw);
    [JsonIgnore] public int DiffLimit => AchievementExcel.ReadInt(DiffLimitRaw);
    [JsonIgnore] public IEnumerable<uint> Mutex => BossExcelRead.Ints(MutexRaw);

    public override uint GetId() => ID;
    public override void Loaded() => GameData.BossEntryData[ID] = this;
}

[ResourceEntity("challenge/boss/boss_award.json")]
public class BossAwardExcel : ExcelResource
{
    public uint ID { get; set; }
    public int ScoreCount { get; set; }
    [JsonProperty("ScoreAward")] public JToken? RewardsRaw { get; set; }

    [JsonIgnore] public IReadOnlyList<IReadOnlyList<uint>> Rewards => AchievementExcel.ReadRows(RewardsRaw);

    public override uint GetId() => ID;
    public override void Loaded() => GameData.BossAwardData[ID] = this;
}

internal static class BossExcelRead
{
    public static IEnumerable<uint> Ints(JToken? token) =>
        token is JArray array ? array.Where(v => v.Type == JTokenType.Integer).Select(v => v.Value<uint>()) : [];
}
