using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MikuSB.Database;
using MikuSB.Database.Player;
using MikuSB.Util;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Shop;

// Retail (2026-10-25 shop capture): {"shopId":12,"goodsList":[{"goodsid":1204,"discountindex":0},...]}.
// Shops whose goods all carry a PosIndex listed every configured row (shops 13,14,19,20,25,50-53, including
// shop 50 where two rows share a PosIndex). Shop 12 has unslotted weighted goods and listed exactly Columns(12)
// rows: one per PosIndex group plus a weighted draw from the unslotted pool. That rolled list is kept per player
// until the shop's RefreshTime (UTC+8). Retail list order and discountindex values other than 0 are not reproduced.
[CallGSApi("ShopLogic_GetGoodsList")]
public class ShopLogic_GetGoodsList : CallGSHandler<ShopGetGoodsListParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, ShopGetGoodsListParam req)
    {
        if (req == null || req.ShopId == 0)
            return Task.FromResult(CallGSResult.Error("error.BadParam"));

        var state = DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(context.Player.Uid);
        var ids = ResolveGoods(req.ShopId, state, DateTimeOffset.UtcNow, out var changed);
        if (changed)
            DatabaseHelper.SaveDatabaseType(state);

        var goodsList = new JsonArray();
        foreach (var id in ids)
            goodsList.Add(new JsonObject { ["goodsid"] = id, ["discountindex"] = 0 });
        var response = new JsonObject { ["shopId"] = req.ShopId, ["goodsList"] = goodsList };
        return Task.FromResult(CallGSResult.Ok(response.ToJsonString()));
    }

    public static List<uint> ResolveGoods(uint shopId, PreservationProgressData state, DateTimeOffset now, out bool changed)
    {
        changed = false;
        var rows = ShopConfig.GoodsRows(shopId);
        if (!rows.Any(row => ShopConfig.PosIndex(row) == null))
            return rows.Select(row => CapturedGameplay.UInt(row, "GoodsId")).ToList();

        var tab = ShopConfig.TabRows().FirstOrDefault(t => CapturedGameplay.UInt(t, "ShopId") == shopId);
        var window = RefreshWindow(tab, now);
        var key = $"shop:{shopId}";
        if (state.Values.TryGetValue(key, out var saved) && saved.Split('|') is [var stamp, var list] &&
            stamp == window.ToUnixTimeSeconds().ToString())
            return list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();

        var picked = Roll(rows, (int)CapturedGameplay.UInt(tab, "Columns"), new Random());
        state.Values[key] = $"{window.ToUnixTimeSeconds()}|{string.Join(',', picked)}";
        changed = true;
        return picked;
    }

    public static List<uint> Roll(IReadOnlyList<JsonNode> rows, int columns, Random random)
    {
        var picked = new List<uint>();
        foreach (var group in rows.Where(r => ShopConfig.PosIndex(r) != null).GroupBy(r => ShopConfig.PosIndex(r)).OrderBy(g => g.Key))
        {
            var choice = Weighted(group.ToList(), random);
            if (choice != null)
                picked.Add(CapturedGameplay.UInt(choice, "GoodsId"));
        }

        var pool = rows.Where(r => ShopConfig.PosIndex(r) == null).ToList();
        var draws = new List<JsonNode>();
        while (picked.Count + draws.Count < columns && pool.Count > 0)
        {
            var choice = Weighted(pool, random)!;
            pool.Remove(choice);
            draws.Add(choice);
        }

        picked.AddRange(rows.Where(draws.Contains).Select(r => CapturedGameplay.UInt(r, "GoodsId")));
        return picked;
    }

    private static JsonNode? Weighted(IReadOnlyList<JsonNode> rows, Random random)
    {
        var total = rows.Sum(r => Math.Max(1L, CapturedGameplay.UInt(r, "Weight")));
        var roll = random.NextInt64(total);
        foreach (var row in rows)
        {
            roll -= Math.Max(1L, CapturedGameplay.UInt(row, "Weight"));
            if (roll < 0)
                return row;
        }

        return rows.LastOrDefault();
    }

    // RefreshTime is "[HHmm]" in UTC+8; RefreshRule 1/2 semantics are unverified, so both refresh daily.
    private static DateTimeOffset RefreshWindow(JsonNode? tab, DateTimeOffset now)
    {
        var text = tab?["RefreshTime"]?.ToString().Trim('[', ']');
        var hhmm = text is { Length: 4 } && int.TryParse(text, out var parsed) ? parsed : 400;
        var local = now.ToOffset(CapturedGameplay.ConfigTimeOffset);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, hhmm / 100, hhmm % 100, 0, CapturedGameplay.ConfigTimeOffset);
        return start > local ? start.AddDays(-1) : start;
    }
}

public static class ShopConfig
{
    public static IReadOnlyList<JsonNode> TabRows() => Optional("shop/shop_tab.json");

    public static IReadOnlyList<JsonNode> DlcShopRows() => Optional("dlc/dlc_shop.json");

    public static IReadOnlyList<JsonNode> GoodsRows(uint shopId) =>
        Optional("shop/goods.json").Concat(Optional("dlc/dlc_goods.json"))
            .Where(r => CapturedGameplay.UInt(r, "ShopId") == shopId).ToList();

    public static uint? PosIndex(JsonNode row) =>
        row["PosIndex"] is JsonValue v && v.TryGetValue<uint>(out var pos) ? pos : null;

    private static IReadOnlyList<JsonNode> Optional(string relative) =>
        File.Exists(Path.Combine(ConfigManager.Config.Path.ResourcePath, relative))
            ? CapturedGameplay.Rows(relative).ToList()
            : [];
}

public sealed class ShopGetGoodsListParam
{
    [JsonPropertyName("nShopId")]
    public uint ShopId { get; set; }
}
