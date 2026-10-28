using System.Text.Json.Nodes;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Shop;

// Returns {nBegin, nEnd} per shop as a Lua array indexed by ShopId, null where no shop is configured.
// Retail (2026-10-25 shop capture): 404 entries built from shop_tab Begin/End and dlc_shop StartTime/EndTime,
// read as UTC+8. Shops newer than the local resource set (e.g. 307/308) only appear once the resources are updated.
[CallGSApi("ShopLogic_GetOpenTime")]
public class ShopLogic_GetOpenTime : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        return Task.FromResult(CallGSResult.Ok(BuildResponse().ToJsonString()));
    }

    public static JsonArray BuildResponse()
    {
        var windows = new SortedDictionary<uint, (long Begin, long End)>();
        void Add(IEnumerable<JsonNode> rows, string beginField, string endField)
        {
            foreach (var row in rows)
            {
                var id = CapturedGameplay.UInt(row, "ShopId");
                if (id != 0 && CapturedGameplay.TryReadTime(row[beginField], out var begin) &&
                    CapturedGameplay.TryReadTime(row[endField], out var end))
                    windows[id] = (begin.ToUnixTimeSeconds(), end.ToUnixTimeSeconds());
            }
        }

        Add(ShopConfig.TabRows(), "Begin", "End");
        Add(ShopConfig.DlcShopRows(), "StartTime", "EndTime");

        var response = new JsonArray();
        if (windows.Count == 0)
            return response;
        for (var i = 0u; i < windows.Keys.Max(); i++)
            response.Add(null);
        foreach (var (id, (begin, end)) in windows)
            response[(int)id - 1] = new JsonObject { ["nBegin"] = begin, ["nEnd"] = end };
        return response;
    }
}
