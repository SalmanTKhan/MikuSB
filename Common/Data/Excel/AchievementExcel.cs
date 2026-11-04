using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MikuSB.Data.Excel;

[ResourceEntity("achievement/achievement.json")]
public class AchievementExcel : ExcelResource
{
    public uint ID { get; set; }
    public int Group { get; set; }
    [JsonProperty("ChapterGroup")] public JToken? ChapterRaw { get; set; }
    [JsonProperty("PreID")] public JToken? PreIdRaw { get; set; }
    [JsonProperty("Type")] public JToken? TypeRaw { get; set; }
    [JsonProperty("CheckValue")] public JToken? CheckValueRaw { get; set; }
    [JsonProperty("Param")] public JToken? ParamRaw { get; set; }
    [JsonProperty("Rewards")] public JToken? RewardsRaw { get; set; }
    [JsonProperty("AchievementPoint")] public JToken? PointRaw { get; set; }

    [JsonIgnore] public int Chapter => ReadInt(ChapterRaw);
    [JsonIgnore] public uint PreId => (uint)ReadInt(PreIdRaw);
    [JsonIgnore] public int Type => ReadInt(TypeRaw);
    [JsonIgnore] public uint CheckValue => CheckValueRaw is { Type: JTokenType.Integer } ? CheckValueRaw.Value<uint>() : 1;
    [JsonIgnore] public int Point => ReadInt(PointRaw);
    [JsonIgnore] public IReadOnlyList<IReadOnlyList<uint>> Rewards => ReadRows(RewardsRaw);

    public int ParamAt(int index) =>
        ParamRaw is JArray array && index < array.Count && array[index].Type == JTokenType.Integer ? array[index].Value<int>() : 0;

    /// <summary>Param {1, {level}}: the stage a type 350 task needs cleared.</summary>
    [JsonIgnore]
    public uint ParamLevel => ParamRaw is JArray { Count: > 1 } array && array[1] is JArray { Count: > 0 } ids && ids[0].Type == JTokenType.Integer ? ids[0].Value<uint>() : 0;

    public override uint GetId() => ID;

    public override void Loaded()
    {
        GameData.AchievementData[ID] = this;
    }

    internal static int ReadInt(JToken? token) => token is { Type: JTokenType.Integer } ? token.Value<int>() : 0;

    internal static IReadOnlyList<IReadOnlyList<uint>> ReadRows(JToken? token) =>
        token is JArray rows
            ? rows.OfType<JArray>().Where(r => r.All(v => v.Type == JTokenType.Integer)).Select(r => (IReadOnlyList<uint>)r.Select(v => v.Value<uint>()).ToArray()).ToArray()
            : [];
}

[ResourceEntity("achievement/point_reward.json")]
public class AchievementPointRewardExcel : ExcelResource
{
    public int Group { get; set; }
    [JsonProperty("AchievementPoint")] public JToken? PointRaw { get; set; }
    [JsonProperty("Rewards")] public JToken? RewardsRaw { get; set; }

    [JsonIgnore] public int Point => AchievementExcel.ReadInt(PointRaw);
    [JsonIgnore] public IReadOnlyList<IReadOnlyList<uint>> Rewards => AchievementExcel.ReadRows(RewardsRaw);

    public override uint GetId() => (uint)Point;

    public override void Loaded()
    {
        if (Group == 1 && Point > 0) GameData.AchievementPointRewardData.Add(this);
    }
}
