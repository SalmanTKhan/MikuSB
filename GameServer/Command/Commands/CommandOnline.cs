using System.Text;
using MikuSB.Database.Account;
using MikuSB.Enums.Player;
using MikuSB.GameServer.Server;

namespace MikuSB.GameServer.Command.Commands;

[CommandInfo("online", "List players currently logged in.", "Usage: /online", ["players", "who"], [PermEnum.Admin, PermEnum.Support])]
public class CommandOnline : ICommands
{
    [CommandDefault]
    public async ValueTask List(CommandArg arg)
    {
        var online = Listener.GetOnlinePlayers();
        if (online.Count == 0)
        {
            await arg.SendMsg("No players online.");
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Online players ({online.Count}):");
        foreach (var con in online)
        {
            var player = con.Player!;
            var account = AccountData.GetAccountByUid(player.Uid);
            builder.AppendLine($"UID {player.Uid} - {player.Data.Name} ({account?.Username ?? "?"})");
        }

        builder.Append("Target a player with @<uid>, e.g. /quest complete_all on @1");
        await arg.SendMsg(builder.ToString());
    }
}
