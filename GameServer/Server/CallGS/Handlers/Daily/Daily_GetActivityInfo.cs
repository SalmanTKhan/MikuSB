using System.Text.Json.Nodes;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Daily;

// Returns the Activity windows of daily/daily.json as a Lua array indexed by daily ID.
// Retail (2026-10-25 capture): 99 entries, IDs 1-8 and 99 set to [[20220331,20220431,3]], the rest null.
[CallGSApi("Daily_GetActivityInfo")]
public class Daily_GetActivityInfo : CallGSHandler
{
    private static readonly Logger Logger = new("Daily");

    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        return Task.FromResult(CallGSResult.Ok(BuildResponse(ConfigManager.Config.Path.ResourcePath).ToJsonString()));
    }

    public static JsonArray BuildResponse(string resourceRoot)
    {
        var response = new JsonArray();
        var path = Path.Combine(resourceRoot, "daily", "daily.json");
        if (!File.Exists(path))
        {
            Logger.Warn($"Daily config not found: {path}");
            return response;
        }

        var rows = (JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(row => (Id: row["ID"] is JsonValue v && v.TryGetValue<int>(out var id) ? id : 0, Activity: row["Activity"]))
            .Where(row => row.Id > 0 && row.Activity is JsonArray)
            .ToList();
        if (rows.Count == 0)
            return response;

        for (var i = 0; i < rows.Max(row => row.Id); i++)
            response.Add(null);
        foreach (var (id, activity) in rows)
            response[id - 1] = activity!.DeepClone();
        return response;
    }
}
