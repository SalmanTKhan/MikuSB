using System.Collections.Concurrent;
using System.Net;
using MikuSB.Util;
using Yggdrasil.Network.TCP;

namespace MikuSB.TcpSharp;

public class SocketListener
{
    public static readonly ConcurrentDictionary<long, SocketConnection> Connections = new();
    private static Action? stopListener;
    private static long nextId;
    private const int MaxConnections = 1024;
    private const int MaxConnectionsPerAddress = 32;
    public static IPEndPoint? LocalEndPoint { get; private set; }

    public static void StartListener<T>() where T : SocketConnection, new()
    {
        if (stopListener != null) throw new InvalidOperationException("Listener already started.");
        var config = ConfigManager.Config.GameServer;
        var acceptor = new TcpConnectionAcceptor<T>(config.BindAddress, config.Port);
        acceptor.ConnectionChecker = connection =>
        {
            var address = connection.RemoteEndPoint.Address;
            if (Connections.Count < MaxConnections
                && Connections.Values.Count(c => c.RemoteEndPoint.Address.Equals(address)) < MaxConnectionsPerAddress)
                return new ConnectionCheck(ConnectionCheckResult.Accept, "");

            new Logger("GameServer").Warn($"Refused connection from {address}: connection limit reached.");
            return new ConnectionCheck(ConnectionCheckResult.Reject, "Connection limit reached.");
        };
        acceptor.ConnectionAccepted += connection =>
        {
            connection.ConnectionId = Interlocked.Increment(ref nextId);
            Connections[connection.ConnectionId] = connection;
        };
        acceptor.AcceptionException += ex => new Logger("GameServer").Error("Accept failed", ex);
        acceptor.Listen(100);
        LocalEndPoint = acceptor.LocalEndPoint;
        stopListener = acceptor.Stop;
        new Logger("GameServer").Info($"Yggdrasil listening on {acceptor.LocalEndPoint}");
    }
    public static void StopListener()
    {
        Interlocked.Exchange(ref stopListener, null)?.Invoke();
        foreach (var connection in Connections.Values) connection.Stop(true);
    }
    public static SocketConnection? GetConnectionByEndPoint(IPEndPoint ep) =>
        Connections.Values.FirstOrDefault(c => c.RemoteEndPoint.Equals(ep));
    public static void UnregisterConnection(SocketConnection connection) => Connections.TryRemove(connection.ConnectionId, out _);
}
