using MikuSB.Database;
using MikuSB.Database.Mail;
using MikuSB.GameServer.Game.Player;
using MikuSB.GameServer.Game.Shop;
using MikuSB.Proto;
using MikuSB.Util.Extensions;

namespace MikuSB.GameServer.Game.Mail;

/// <summary>
/// The mailbox behind ReqReadMail / ReqMailAttachment / ReqDelMail. Player.mail_box carries {mid, stat, time, expiration} in the login
/// snapshot and NtfSyncNewMail a mail's contents (core.Mail). The retail wire bodies of the three requests are not in the protocol
/// descriptors (the game's native <c>me:ReadMail</c> writes them); this server takes core.IDArray and answers core.PlayerMail, with mid 0
/// when refused. Unverified against the retail client.
/// </summary>
public sealed class MailManager(PlayerInstance player) : BasePlayerManager(player)
{
    public MailData Data { get; } = DatabaseHelper.GetInstanceOrCreateNew<MailData>(player.Uid);

    /// <summary>Puts a mail in the box; nothing is sent (the caller is at login, or sends it with <see cref="ToProto"/>).</summary>
    public MailRecord Add(string sender, string title, string message, IEnumerable<IReadOnlyList<uint>> attachments, TimeSpan? life = null)
    {
        var now = (uint)Extensions.GetUnixSec();
        var mail = new MailRecord
        {
            Mid = Data.NextMid++, Sender = sender, Title = title, Message = message, Time = now,
            Expiration = life is { } l ? now + (uint)l.TotalSeconds : 0,
            Attachments = [.. attachments.Select(a => a.ToList())],
        };
        Data.Mails.Add(mail);
        DatabaseHelper.SaveDatabaseType(Data);
        return mail;
    }

    public ValueTask OnEnterGame()
    {
        Data.Mails.RemoveAll(Expired);
        if (!Data.WelcomeSent)
        {
            // The new-account mail, the same as the offline client's: starting gold and credits.
            Add("Command", "Welcome, Adjutant", "Welcome aboard. Here is something to get you started.", [[4, 3, 2, 1, 1600], [4, 3, 1, 1, 100000]]);
            Data.WelcomeSent = true;
            DatabaseHelper.SaveDatabaseType(Data);
        }
        return ValueTask.CompletedTask;
    }

    private static bool Expired(MailRecord mail) => mail.Expiration != 0 && mail.Expiration <= (uint)Extensions.GetUnixSec();

    private MailRecord? Live(ulong mid) => Data.Mails.FirstOrDefault(m => m.Mid == mid && !Expired(m));

    /// <summary>Marks a mail read; null when it is gone.</summary>
    public MailRecord? Read(ulong mid)
    {
        if (Live(mid) is not { } mail) return null;
        if (mail.Stat == (int)MailStat.New) { mail.Stat = (int)MailStat.Readed; DatabaseHelper.SaveDatabaseType(Data); }
        return mail;
    }

    /// <summary>Grants a mail's attachments once. Null when it is gone or already taken or the rows cannot be granted.</summary>
    public async ValueTask<MailRecord?> Claim(ulong mid, NtfSyncPlayer sync)
    {
        if (Live(mid) is not { } mail || mail.Stat == (int)MailStat.Geted || mail.Attachments.Count == 0) return null;
        if (!Player.RewardManager.CanGrantConfiguredRewards(mail.Attachments, out _)) return null;
        await Player.RewardManager.GrantConfiguredRewardsAsync(mail.Attachments, sync);
        mail.Stat = (int)MailStat.Geted;
        ShopService.Save(Player);
        DatabaseHelper.SaveDatabaseType(Data);
        return mail;
    }

    /// <summary>Deletes a mail; an unclaimed attachment keeps it (the game takes the attachments first).</summary>
    public MailRecord? Delete(ulong mid)
    {
        if (Data.Mails.FirstOrDefault(m => m.Mid == mid) is not { } mail) return null;
        if (!Expired(mail) && mail.Attachments.Count > 0 && mail.Stat != (int)MailStat.Geted) return null;
        Data.Mails.Remove(mail);
        DatabaseHelper.SaveDatabaseType(Data);
        return mail;
    }

    public static PlayerMail ToBox(MailRecord mail) => new() { Mid = mail.Mid, Stat = (MailStat)mail.Stat, Time = mail.Time, Expiration = mail.Expiration };

    public static Proto.Mail ToProto(MailRecord mail)
    {
        var proto = new Proto.Mail
        {
            Mid = mail.Mid, Title = mail.Title, Message = mail.Message, Sender = mail.Sender, Pid = (ulong)0, Time = mail.Time,
            Expiration = mail.Expiration, Stat = (MailStat)mail.Stat,
        };
        foreach (var a in mail.Attachments)
            if (a.Count >= 5) proto.Attachments.Add(new SimpleItem { G = a[0], D = a[1], P = a[2], L = a[3], Count = a[4] });
        return proto;
    }
}
