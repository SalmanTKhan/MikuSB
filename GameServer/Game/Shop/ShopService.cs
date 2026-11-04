using System.Text.Json.Nodes;
using MikuSB.Data;
using MikuSB.Database;
using MikuSB.Database.Player;
using MikuSB.GameServer.Game.Player;
using MikuSB.GameServer.Server.CallGS;
using MikuSB.GameServer.Server.CallGS.Handlers.Shop;
using MikuSB.Proto;

namespace MikuSB.GameServer.Game.Shop;

/// <summary>
/// The shop's player state as Shop.lua reads it: string attributes in gid 1, sid = shop id for
/// {shopid, version, listversion, refreshnum, displaynum, refreshtime, tbgoods:[{goodsid, discountindex}]} and sid = 1000 + goods id
/// for {buynum, invalidtime}. A limit resets by its LimitType (1 day, 2 week, 3 month, others never) at 04:00 UTC+8, kept in an extra
/// "window" key the retail script ignores. Discounts (discountindex above 0) are not rolled.
/// </summary>
public static class ShopService
{
    public const uint Gid = 1, GoodsStart = 1000;
    public const int MaxPerPurchase = 99;
    private static readonly TimeSpan DayStart = TimeSpan.FromHours(4);

    /// <summary>The reset window of a limit type at a time.</summary>
    public static long Window(uint limitType, DateTimeOffset now)
    {
        var day = (now.ToOffset(CapturedGameplay.ConfigTimeOffset) - DayStart).Date;
        return limitType switch
        {
            1 => day.Ticks / TimeSpan.TicksPerDay,
            2 => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).Ticks / TimeSpan.TicksPerDay,
            3 => day.Year * 12L + day.Month,
            _ => 0,
        };
    }

    /// <summary>Bought within the goods row's current limit window.</summary>
    public static uint Bought(PlayerInstance player, JsonNode row, DateTimeOffset now)
    {
        var text = player.Attributes.GetStringValue(Gid, GoodsStart + CapturedGameplay.UInt(row, "GoodsId"));
        if (string.IsNullOrEmpty(text) || JsonNode.Parse(text) is not JsonObject saved) return 0;
        var window = saved["window"] is JsonValue w && w.TryGetValue<long>(out var at) ? at : 0;
        return window == Window(CapturedGameplay.UInt(row, "LimitType"), now) ? CapturedGameplay.UInt(saved, "buynum") : 0;
    }

    /// <summary>Records a purchase in its goods attribute, synced.</summary>
    public static void AddBought(PlayerInstance player, JsonNode row, uint count, DateTimeOffset now, NtfSyncPlayer sync)
    {
        var goodsId = CapturedGameplay.UInt(row, "GoodsId");
        var json = new JsonObject
        {
            ["buynum"] = Bought(player, row, now) + count,
            ["invalidtime"] = 0,
            ["window"] = Window(CapturedGameplay.UInt(row, "LimitType"), now),
        };
        player.Attributes.SyncTo(sync, player.Attributes.SetString(Gid, GoodsStart + goodsId, json.ToJsonString()));
    }

    /// <summary>Manual refreshes in today's window.</summary>
    public static uint Refreshes(PreservationProgressData state, uint shopId, DateTimeOffset now) =>
        state.Values.TryGetValue($"shoprefresh:{shopId}", out var saved) && saved.Split('|') is [var window, var count] &&
        window == Window(1, now).ToString() && uint.TryParse(count, out var n) ? n : 0;

    /// <summary>Writes a shop's attribute from its current stock, synced, and returns the stock.</summary>
    public static List<uint> SyncShop(PlayerInstance player, uint shopId, DateTimeOffset now, NtfSyncPlayer? sync)
    {
        var state = DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(player.Uid);
        var ids = ShopLogic_GetGoodsList.ResolveGoods(shopId, state, now, out var changed);
        if (changed) DatabaseHelper.SaveDatabaseType(state);
        var goods = new JsonArray();
        foreach (var id in ids) goods.Add(new JsonObject { ["goodsid"] = id, ["discountindex"] = 0 });
        var tab = ShopConfig.TabRows().Concat(ShopConfig.DlcShopRows()).FirstOrDefault(t => CapturedGameplay.UInt(t, "ShopId") == shopId);
        var json = new JsonObject
        {
            ["shopid"] = shopId,
            ["version"] = CapturedGameplay.UInt(tab, "Version"),
            ["listversion"] = CapturedGameplay.UInt(tab, "ListVersion"),
            ["refreshnum"] = Refreshes(state, shopId, now),
            ["displaynum"] = ids.Count,
            ["refreshtime"] = 0,
            ["tbgoods"] = goods,
        };
        var attr = player.Attributes.SetString(Gid, shopId, json.ToJsonString());
        if (sync != null) player.Attributes.SyncTo(sync, attr);
        return ids;
    }

    /// <summary>Every open regular shop's attribute, for the login snapshot: Shop.lua sells only what its shop attribute lists.</summary>
    public static void SyncOpenShops(PlayerInstance player, DateTimeOffset now)
    {
        foreach (var tab in ShopConfig.TabRows())
            if (CapturedGameplay.UInt(tab, "OnOff") > 0)
                SyncShop(player, CapturedGameplay.UInt(tab, "ShopId"), now, null);
    }

    /// <summary>A price cell: {money, n} or {g, d, p, l, n}; null when the cell is empty.</summary>
    public static uint[]? ReadPrice(JsonNode? cell)
    {
        if (cell is not JsonArray a || a.Count is not (2 or 5)) return null;
        var values = new uint[a.Count];
        for (int i = 0; i < a.Count; ++i)
        {
            if (a[i] is not JsonValue v || !v.TryGetValue<uint>(out values[i])) return null;
        }
        return values;
    }

    /// <summary>The prices a buy pays (ShopLogic.BuyGoods): Calculation 1 lets the buyer pick Price1 / Price2 by nBuyMode, otherwise every price.</summary>
    public static List<uint[]> Prices(JsonNode row, uint buyMode)
    {
        var p1 = ReadPrice(row["Price1"]);
        var p2 = ReadPrice(row["Price2"]);
        var prices = new List<uint[]>();
        if (CapturedGameplay.UInt(row, "Calculation") == 1)
        {
            if ((buyMode == 2 ? p2 : p1) is { } picked) prices.Add(picked);
        }
        else
        {
            if (p1 != null) prices.Add(p1);
            if (p2 != null) prices.Add(p2);
        }
        return prices;
    }

    /// <summary>Whether the player can pay every price <paramref name="times"/> over.</summary>
    public static bool CanPay(PlayerInstance player, IEnumerable<uint[]> prices, uint times)
    {
        // Two prices may charge the same thing, so they are summed before they are compared.
        var money = new Dictionary<uint, ulong>();
        var items = new Dictionary<ulong, ulong>();
        foreach (var price in prices)
        {
            ulong total = (ulong)price[^1] * times;
            if (price.Length == 2) money[price[0]] = money.GetValueOrDefault(price[0]) + total;
            else
            {
                var template = GameResourceTemplateId.FromGdpl(price[0], price[1], price[2], price[3]);
                items[template] = items.GetValueOrDefault(template) + total;
            }
        }
        foreach (var (type, total) in money)
            if (Balance(player, type) < total) return false;
        foreach (var (template, total) in items)
            if ((ulong)player.InventoryManager.InventoryData.Items.Values.Where(i => i.TemplateId == template).Sum(i => (long)i.ItemCount) < total) return false;
        return true;
    }

    private static ulong Balance(PlayerInstance player, uint money) =>
        money == AttrIds.Currency.Vigor ? player.Data.Vigor : player.Attributes.GetValue(AttrIds.Currency.GroupId, AttrIds.Currency.GetSid(money));

    /// <summary>Charges prices already checked by <see cref="CanPay"/>.</summary>
    public static void Pay(PlayerInstance player, IEnumerable<uint[]> prices, uint times, NtfSyncPlayer sync)
    {
        foreach (var price in prices)
        {
            uint total = checked(price[^1] * times);
            if (price.Length == 2) PayMoney(player, price[0], total, sync);
            else PayItem(player, GameResourceTemplateId.FromGdpl(price[0], price[1], price[2], price[3]), total, sync);
        }
    }

    public static void PayMoney(PlayerInstance player, uint money, uint total, NtfSyncPlayer sync)
    {
        if (money == AttrIds.Currency.Vigor)
        {
            player.Data.Vigor -= Math.Min(player.Data.Vigor, total);
            sync.Core[(uint)PlayerCoreAttribute.Vigor] = player.Data.Vigor;
            return;
        }
        var attr = player.Attributes.GetOrCreate(AttrIds.Currency.GroupId, AttrIds.Currency.GetSid(money));
        attr.Val -= Math.Min(attr.Val, total);
        player.Attributes.SyncTo(sync, attr);
        if (money == AttrIds.Currency.Money)
            foreach (var (key, value) in player.BuildMoneySync()) sync.Money[key] = value;
    }

    private static void PayItem(PlayerInstance player, ulong template, uint total, NtfSyncPlayer sync)
    {
        var inventory = player.InventoryManager.InventoryData;
        foreach (var item in inventory.Items.Values.Where(i => i.TemplateId == template).ToList())
        {
            if (total == 0) break;
            uint used = Math.Min(item.ItemCount, total);
            item.ItemCount -= used;
            total -= used;
            sync.Items.Add(item.ToProto());
            if (item.ItemCount == 0) inventory.Items.Remove(item.UniqueId);
        }
    }

    public static void Save(PlayerInstance player)
    {
        DatabaseHelper.SaveDatabaseType(player.Data);
        DatabaseHelper.SaveDatabaseType(player.InventoryManager.InventoryData);
        DatabaseHelper.SaveDatabaseType(player.CharacterManager.CharacterData);
    }
}
