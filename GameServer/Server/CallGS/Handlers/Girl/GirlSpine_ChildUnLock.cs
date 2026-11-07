using MikuSB.Data;
using MikuSB.Database;
using MikuSB.Database.Player;
using MikuSB.GameServer.Game.Player;
using MikuSB.Proto;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Girl;

// Spine node encoding: (MastIdx << 8) | SubIdx stored as uint in CharacterInfo.Spines
// GetStrAttribute(Gid=30, Sid=Detail) stores JSON: { "<Particular>": { "ns": <MastIdx>, "tbn": [0,0], "tbr": [] } }
[CallGSApi("GirlSpine_ChildUnLock")]
public class GirlSpine_ChildUnLock : CallGSHandler<ChildUnLockParam>
{
    private const uint SpineStrAttrGid = AttrIds.Girl.SpineStringGid;

    protected override async Task<CallGSResult> HandleAsync(CallGSContext context, ChildUnLockParam req)
    {
        var player = context.Connection.Player!;

        if (req == null || req.CardId == 0 || req.Info == null || req.Materials == null)
        {
            return CallGSResult.Error("error.BadParam");
        }

        var card = player.CharacterManager.GetCharacterByGUID((uint)req.CardId);
        if (card == null)
        {
            return CallGSResult.Error("error.BadParam");
        }

        var mastIdx = req.Info.Indx;
        var subIdx = req.Info.InSubIdx;
        // The ranges SpineExcel and NodeConditionExcel define; larger values allocate or skip the cost.
        if (mastIdx is < 1 or > 6 || subIdx is < 1 or > 9)
        {
            return CallGSResult.Error("error.BadParam");
        }

        var cardKey = (Detail: (uint)((card.TemplateId >> 16) & 0xFFFF), Particular: (uint)((card.TemplateId >> 32) & 0xFFFF));
        var cardExcel = GameData.CardData.Values.FirstOrDefault(
            x => x.Detail == cardKey.Detail && x.Particular == cardKey.Particular);
        if (cardExcel == null || !GameData.SpineData.TryGetValue(cardExcel.SpineId, out var spineExcel))
            return CallGSResult.Error("error.BadParam");
        var nodeCondId = spineExcel.GetNodeReq(mastIdx);
        if (nodeCondId == 0 || !GameData.NodeConditionData.TryGetValue(nodeCondId, out var nodeCond))
            return CallGSResult.Error("error.BadParam");

        // Spines[MastIdx-1] is a bitmask; bit (SubIdx-1) = 1 means that sub-node is unlocked.
        // GetSpine(MastIdx, SubIdx) checks (Spines[MastIdx-1] & (1 << (SubIdx-1))) != 0
        int spineListIdx = mastIdx - 1;
        uint spineBit = 1u << (subIdx - 1);

        while (card.Spines.Count <= spineListIdx)
            card.Spines.Add(0);

        if ((card.Spines[spineListIdx] & spineBit) != 0)
        {
            return CallGSResult.Error("tip.girlcard_alread_break");
        }

        // Consume materials
        var requestedMaterials = new Dictionary<ulong, uint>();
        // The table sets the price; the client's list is only what it thinks it costs.
        foreach (var row in nodeCond.GetNodeCost(subIdx))
        {
            if (row.Count < 5) continue;
            var tid = GameResourceTemplateId.FromGdpl((uint)row[0], (uint)row[1], (uint)row[2], (uint)row[3]);
            requestedMaterials[tid] = requestedMaterials.GetValueOrDefault(tid) + (uint)row[4];
        }

        var syncItems = new List<Item>();
        foreach (var (tid, count) in requestedMaterials)
        {
            var item = player.InventoryManager.InventoryData.Items.Values.FirstOrDefault(x => x.TemplateId == tid);
            if (item == null || item.ItemCount < count)
            {
                return CallGSResult.Error("tip.not_material");
            }
        }

        foreach (var (tid, count) in requestedMaterials)
        {
            var item = player.InventoryManager.InventoryData.Items.Values.First(x => x.TemplateId == tid);
            item.ItemCount -= count;
            var proto = item.ToProto();
            if (item.ItemCount == 0)
            {
                player.InventoryManager.InventoryData.Items.Remove(item.UniqueId);
                proto.Count = 0;
            }
            syncItems.Add(proto);
        }

        // Unlock the spine node by setting the corresponding bit
        card.Spines[spineListIdx] |= spineBit;
        syncItems.Add(card.ToProto());

        // Extract Detail and Particular from TemplateId for StrAttr
        var cardDetail = (uint)((card.TemplateId >> 16) & 0xFFFF);
        var cardParticular = (uint)((card.TemplateId >> 32) & 0xFFFF);

        // Build and persist StrAttr JSON: { "<particular>": { "ns": mastIdx, "tbn": [0,0], "tbr": [] } }
        var strAttr = UpdateSpineStrAttr(player, cardDetail, cardParticular, mastIdx);

        DatabaseHelper.SaveDatabaseType(player.InventoryManager.InventoryData);
        DatabaseHelper.SaveDatabaseType(player.CharacterManager.CharacterData);
        DatabaseHelper.SaveDatabaseType(player.Data);

        var sync = new NtfSyncPlayer();
        sync.Items.AddRange(syncItems);

        // Push the blob on every channel the client can learn it from - the reply's own sync plus the
        // attribute notifications (see GirlCard_ReceiveProLevelAward for the reasoning). The panel's
        // "通导中" node reads ns through the client's cached copy of GetStrAttribute(30, Detail)
        // (Item/Spine.lua:103-107, 110-123), so a store that is not refreshed keeps showing the
        // previous stage.
        player.Attributes.SyncTo(sync, strAttr);

        var strSync = new NtfSyncPlayer();
        player.Attributes.SyncTo(strSync, strAttr);
        await player.SendPacket(CmdIds.NtfSyncAttr, strSync);
        await player.SendPacket(CmdIds.NtfSetStrAttr, new NtfSetStrAttr
        {
            Gid = SpineStrAttrGid,
            Sid = cardDetail,
            Val = strAttr.Val
        });

        var rsp = $"{{\"tb\":{{\"D\":{cardDetail},\"pId\":{req.CardId},\"MastIdx\":{mastIdx},\"SubIdx\":{subIdx}}}}}";
        return CallGSResult.Ok(rsp, sync);
    }

    private static PlayerStrAttr UpdateSpineStrAttr(PlayerInstance player, uint detail, uint particular, int mastIdx)
    {
        var existing = player.Attributes.GetString(SpineStrAttrGid, detail);
        var raw = existing?.Val ?? "{}";

        var root = JsonSerializer.Deserialize<Dictionary<string, SpineStrData>>(raw)
                   ?? new Dictionary<string, SpineStrData>();

        var key = particular.ToString();
        if (!root.TryGetValue(key, out var entry))
            entry = new SpineStrData();

        entry.Ns = mastIdx;
        root[key] = entry;

        return player.Attributes.SetString(SpineStrAttrGid, detail, JsonSerializer.Serialize(root));
    }
}

public sealed class ChildUnLockParam
{
    [JsonPropertyName("pId")]
    public int CardId { get; set; }

    [JsonPropertyName("tbInfo")]
    public NodeInfo? Info { get; set; }

    [JsonPropertyName("tbMat")]
    public List<List<int>> Materials { get; set; } = [];
}

public sealed class NodeInfo
{
    [JsonPropertyName("Indx")]
    public int Indx { get; set; }

    [JsonPropertyName("InSubIdx")]
    public int InSubIdx { get; set; }
}

internal sealed class SpineStrData
{
    [JsonPropertyName("ns")]
    public int Ns { get; set; } = 0;

    [JsonPropertyName("tbn")]
    public List<int> Tbn { get; set; } = [0, 0];

    [JsonPropertyName("tbr")]
    public List<int> Tbr { get; set; } = [];
}
