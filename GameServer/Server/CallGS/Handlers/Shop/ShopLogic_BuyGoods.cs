using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MikuSB.Database;
using MikuSB.Database.Player;
using MikuSB.GameServer.Game.Shop;
using MikuSB.Proto;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Shop;

// Shop.lua ShopLogic.BuyGoods: {nGoodsId, nBuyMode, nCount} -> {tbGoods: [g,d,p,l,n]} with the stock, price and limit checks repeated here.
// The script's errors: ui.TxtNotOpen (not on sale), ui.TxtLimitBuy, tip.NotEnoughItem / tip.gold_not_enough.
[CallGSApi("ShopLogic_BuyGoods")]
public class ShopLogic_BuyGoods : CallGSHandler<ShopBuyGoodsParam>
{
    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, ShopBuyGoodsParam req)
    {
        if (req.GoodsId == 0 || req.Count is 0 or > ShopService.MaxPerPurchase) return CallGSResult.Error("error.BadParam");
        var player = context.Player;
        var now = DateTimeOffset.UtcNow;
        var row = ShopConfig.GoodsRow(req.GoodsId);
        if (row == null) return CallGSResult.Error("ui.TxtNotOpen");
        var shopId = CapturedGameplay.UInt(row, "ShopId");
        var state = DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(player.Uid);
        var stock = ShopLogic_GetGoodsList.ResolveGoods(shopId, state, now, out var rolled);
        if (rolled) DatabaseHelper.SaveDatabaseType(state);
        if (!stock.Contains(req.GoodsId) || !OnSale(row, now)) return CallGSResult.Error("ui.TxtNotOpen");

        var limitType = CapturedGameplay.UInt(row, "LimitType");
        var limit = row["LimitNum"] is JsonValue l && l.TryGetValue<int>(out var n) ? n : -1;
        if (limitType > 0 && limit != -1 && ShopService.Bought(player, row, now) + req.Count > (uint)limit)
            return CallGSResult.Error("ui.TxtLimitBuy");

        if (row["GDPLN"] is not JsonArray { Count: >= 4 } gdpln) return CallGSResult.Error("error.BadParam");
        uint[] item = [.. gdpln.Select(v => v!.GetValue<uint>())];
        uint perBuy = item.Length > 4 ? Math.Max(item[4], 1u) : 1u;
        IReadOnlyList<uint> grant = [item[0], item[1], item[2], item[3], checked(perBuy * req.Count)];
        if (!player.RewardManager.CanGrantConfiguredRewards([grant], out _)) return CallGSResult.Error("error.BadParam");

        var prices = ShopService.Prices(row, req.BuyMode);
        if (!ShopService.CanPay(player, prices, req.Count))
            return CallGSResult.Error(prices.Any(p => p.Length > 2) ? "tip.NotEnoughItem" : "tip.gold_not_enough");

        var sync = new NtfSyncPlayer();
        ShopService.Pay(player, prices, req.Count, sync);
        var (granted, _) = await player.RewardManager.GrantConfiguredRewardsStrictAsync([grant], sync);
        ShopService.AddBought(player, row, req.Count, now, sync);
        ShopService.Save(player);
        return CallGSResult.Ok(new JsonObject { ["nGoodsId"] = req.GoodsId, ["tbGoods"] = granted.Count > 0 ? granted[0]!.DeepClone() : new JsonArray() }, sync);
    }

    // A discount row makes Begin / End the discount's window instead (ShopLogic.GetGoodsInfo); the goods stay on sale.
    private static bool OnSale(JsonNode row, DateTimeOffset now) =>
        row["Discount"] is JsonArray ||
        ((!CapturedGameplay.TryReadTime(row["Begin"], out var begin) || now >= begin) && (!CapturedGameplay.TryReadTime(row["End"], out var end) || now < end));
}

// Shop.lua ShopLogic.UpdateShop: {nShopId} rerolls a rolled shop for RefreshFee[refreshes + 1] of MoneyType, at most RefreshLimits a day.
// Its reply is the goods list, as ShopLogic_GetGoodsList's.
[CallGSApi("ShopLogic_UpdateShop")]
public class ShopLogic_UpdateShop : CallGSHandler<ShopGetGoodsListParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, ShopGetGoodsListParam req)
    {
        var tab = ShopConfig.TabRows().Concat(ShopConfig.DlcShopRows()).FirstOrDefault(t => CapturedGameplay.UInt(t, "ShopId") == req.ShopId);
        if (tab == null) return Task.FromResult(CallGSResult.Error("error.BadParam"));
        var player = context.Player;
        var now = DateTimeOffset.UtcNow;
        var state = DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(player.Uid);
        var done = ShopService.Refreshes(state, req.ShopId, now);
        if (done >= CapturedGameplay.UInt(tab, "RefreshLimits")) return Task.FromResult(CallGSResult.Error("tip.refresh_run_out"));
        if (!ShopLogic_GetGoodsList.Rolled(req.ShopId)) return Task.FromResult(CallGSResult.Error("error.BadParam"));

        var sync = new NtfSyncPlayer();
        var fee = tab["RefreshFee"] is JsonArray fees && done < fees.Count && fees[(int)done] is JsonValue f && f.TryGetValue<uint>(out var v) ? v : 0;
        var money = CapturedGameplay.UInt(tab, "MoneyType");
        if (fee > 0)
        {
            uint[] price = [money, fee];
            if (!ShopService.CanPay(player, [price], 1)) return Task.FromResult(CallGSResult.Error("tip.gold_not_enough"));
            ShopService.Pay(player, [price], 1, sync);
        }
        ShopLogic_GetGoodsList.ForgetRoll(req.ShopId, state);
        state.Values[$"shoprefresh:{req.ShopId}"] = $"{ShopService.Window(1, now)}|{done + 1}";
        DatabaseHelper.SaveDatabaseType(state);
        var ids = ShopService.SyncShop(player, req.ShopId, now, sync);
        ShopService.Save(player);
        var goods = new JsonArray();
        foreach (var id in ids) goods.Add(new JsonObject { ["goodsid"] = id, ["discountindex"] = 0 });
        // Shop.lua registers no ShopLogic_UpdateShop callback: the list goes out under ShopLogic_GetGoodsList, which refreshes the open screen.
        return Task.FromResult(CallGSResult.Ok(new JsonObject { ["shopId"] = req.ShopId, ["goodsList"] = goods }, sync, "ShopLogic_GetGoodsList"));
    }
}

public sealed class ShopBuyGoodsParam
{
    [JsonPropertyName("nGoodsId")] public uint GoodsId { get; set; }
    [JsonPropertyName("nBuyMode")] public uint BuyMode { get; set; }
    [JsonPropertyName("nCount")] public uint Count { get; set; } = 1;
}
