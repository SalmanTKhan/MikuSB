using MikuSB.TcpSharp;

namespace MikuSB.GameServer.Server;

public class Listener : SocketListener
{
    public static Connection? GetActiveConnection(int uid)
    {
        var con = Connections.Values.FirstOrDefault(c =>
            (c as Connection)?.Player?.Uid == uid && c.State == SessionStateEnum.ACTIVE) as Connection;
        return con;
    }

    public static List<Connection> GetOnlinePlayers() =>
        Connections.Values
            .OfType<Connection>()
            .Where(c => c.Player != null && c.State == SessionStateEnum.ACTIVE)
            .OrderBy(c => c.Player!.Uid)
            .ToList();
}