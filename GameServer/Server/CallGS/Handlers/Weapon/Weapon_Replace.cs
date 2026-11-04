using MikuSB.Data;
using MikuSB.Database;
using MikuSB.Proto;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Weapon;

// {CardId, Id}: arms a card. The weapon's type (Detail) must match her default weapon's. A weapon another card holds is swapped: she takes
// this card's old weapon, so no card is left unarmed.
[CallGSApi("Weapon_Replace")]
public class Weapon_Replace : CallGSHandler<WeaponReplaceParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, WeaponReplaceParam req)
    {
        var player = context.Connection.Player!;
        var card = player.CharacterManager.GetCharacterByGUID((uint)req.CardId);
        var weapon = player.InventoryManager.GetWeaponItem((uint)req.Id);
        if (card == null || weapon == null)
            return Task.FromResult(CallGSResult.Error("error.BadParam"));

        var template = GameData.CardData.Values.FirstOrDefault(x =>
            GameResourceTemplateId.FromGdpl(x.Genre, x.Detail, x.Particular, x.Level) == card.TemplateId);
        if (template?.DefaultWeaponGPDL.Count >= 4 && GameResourceTemplateId.FromGdpl(
                template.DefaultWeaponGPDL[0], template.DefaultWeaponGPDL[1], template.DefaultWeaponGPDL[2], template.DefaultWeaponGPDL[3]) is var defaultWeapon
            && Detail(defaultWeapon) != Detail(weapon.TemplateId))
            return Task.FromResult(CallGSResult.Error("error.BadParam"));

        var sync = new NtfSyncPlayer();
        if (card.WeaponUniqueId != weapon.UniqueId)
        {
            var old = card.WeaponUniqueId == 0 ? null : player.InventoryManager.GetWeaponItem(card.WeaponUniqueId);
            var holder = player.CharacterManager.CharacterData.Characters.FirstOrDefault(c => c.Guid != card.Guid && c.WeaponUniqueId == weapon.UniqueId);
            if (holder != null)
            {
                holder.WeaponUniqueId = old?.UniqueId ?? 0;
                sync.Items.Add(holder.ToProto());
            }
            if (old != null) old.EquipAvatarId = holder?.Guid ?? 0;
            card.WeaponUniqueId = weapon.UniqueId;
            weapon.EquipAvatarId = card.Guid;
            if (old != null) sync.Items.Add(old.ToProto());
            sync.Items.Add(weapon.ToProto());
            DatabaseHelper.SaveDatabaseType(player.CharacterManager.CharacterData);
            DatabaseHelper.SaveDatabaseType(player.InventoryManager.InventoryData);
        }
        sync.Items.Add(card.ToProto());
        return Task.FromResult(CallGSResult.Ok("{}", sync));
    }

    // A template id packs genre in the low 16 bits and detail in the next.
    private static ulong Detail(ulong templateId) => (templateId >> 16) & 0xFFFF;
}

public sealed class WeaponReplaceParam
{
    public int CardId { get; set; }
    public int Id { get; set; }
}
