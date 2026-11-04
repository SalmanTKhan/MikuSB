using SqlSugar;

namespace MikuSB.Database.Mail;

/// <summary>A player's mailbox (core.Mail on the wire). Stat: 0 new, 1 read, 2 attachments taken (MailStat).</summary>
[SugarTable("mail_data")]
public sealed class MailData : BaseDatabaseDataHelper
{
    public ulong NextMid { get; set; } = 1;
    public bool WelcomeSent { get; set; }
    [SugarColumn(IsJson = true)] public List<MailRecord> Mails { get; set; } = [];
}

public sealed class MailRecord
{
    public ulong Mid { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Sender { get; set; } = "";
    public uint Time { get; set; }
    /// <summary>Unix time it expires; 0: never.</summary>
    public uint Expiration { get; set; }
    public int Stat { get; set; }
    /// <summary>Attachments as {g, d, p, l, count}.</summary>
    public List<List<uint>> Attachments { get; set; } = [];
}
