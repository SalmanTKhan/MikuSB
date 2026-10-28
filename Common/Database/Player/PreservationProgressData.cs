using SqlSugar;
namespace MikuSB.Database.Player;

// Local emulator state; these keys are not invented retail attribute IDs.
[SugarTable("PreservationProgress")]
public sealed class PreservationProgressData : BaseDatabaseDataHelper
{
    [SugarColumn(IsJson = true)] public Dictionary<string, string> Values { get; set; } = [];
    [SugarColumn(IsJson = true)] public HashSet<string> Claims { get; set; } = [];
}
