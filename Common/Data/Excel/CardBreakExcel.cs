using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MikuSB.Data.Excel;

/// <summary>Card breakthrough costs. Columns are named {breakLevel}Items{option}, each holding GDPLN rows.</summary>
[ResourceEntity("item/cardbreak/break.json")]
public class CardBreakExcel : ExcelResource
{
    [JsonProperty("ID")] public int Id { get; set; }

    [JsonExtensionData] public IDictionary<string, JToken> Columns { get; set; } = new Dictionary<string, JToken>();

    public IEnumerable<List<List<int>>> GetOptions(uint breakLevel) => Columns
        .Where(column => column.Key.StartsWith($"{breakLevel}Items", StringComparison.Ordinal)
                         && column.Value is JArray { Count: > 0 })
        .Select(column => column.Value.ToObject<List<List<int>>>() ?? []);

    public override uint GetId() => (uint)Id;

    public override void Loaded()
    {
        GameData.CardBreakData[Id] = this;
    }
}
