using MikuSB.Proto;
using MikuSB.Enums.Packet;
using MikuSB.GameServer.Game.Player;
using MikuSB.GameServer.Server.Packet;
using MikuSB.TcpSharp;
using MikuSB.Util;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace MikuSB.GameServer.Server;

public class Connection : SocketConnection
{
    private static readonly Logger Logger = new("GameServer");

    public PlayerInstance? Player { get; set; }
    private int stopping;

    /// <summary>online/online.json Id the client asked for with ReqOnlineRoom(303), 0 when unknown.</summary>
    public uint OnlineRoomId { get; set; }

    /// <summary>Team id the client asked for with ReqOnlineRoom(303) (10 = Online.TeamId).</summary>
    public uint OnlineRoomTeamId { get; set; }

    /// <summary>
    /// Set once a room start (ReqOnlineRoomStart or the solo auto-start in HandlerReqOnlineRoom) has sent
    /// NtfOnlineStart, so the two paths can never travel the client twice.
    /// </summary>
    public bool OnlineRoomStarted { get; set; }

    private static readonly HashSet<string> DummyPacketNames =
    [
        
    ];

    public override async void Start()
    {
        Logger.Info($"New connection from {RemoteEndPoint}.");
        State = SessionStateEnum.WAITING_FOR_TOKEN;
        await ReceiveLoop();
    }

    public override void Stop(bool isServerStop = false)
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        Packet.Recv.Online.OnlineRoomService.ForgetConnection(this);
        Player?.OnLogoutAsync();
        SocketListener.UnregisterConnection(this);
        base.Stop(isServerStop);
    }

    public static int GetInt32(byte[] buf, int index)
    {
        int networkValue = BitConverter.ToInt32(buf, index);
        return IPAddress.NetworkToHostOrder((int)networkValue);
    }

    protected async Task ReceiveLoop()
    {
        try
        {
            using var stream = IncomingStream;

            while (SocketConnected())
            {
                // A silent connection is dropped; an anonymous one quickly and with a small frame limit.
                var loggedIn = Player != null;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(CancelToken.Token);
                idle.CancelAfter(loggedIn ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
                var codec = loggedIn ? new PacketCodec() : new PacketCodec { MaxLength = 64 * 1024 };
                var decodedPacket = await codec.ReadPacketAsync(stream, idle.Token);

                if (decodedPacket == null)
                {
                    Logger.Info("Client disconnected");
                    break;
                }

                switch (decodedPacket.Framing)
                {
                    case PacketFraming.FourByteLittleEndianLength:
                    case PacketFraming.TwoByteBigEndianLength:
                        Framing = decodedPacket.Framing;
                        LogPacket("Recv", decodedPacket.CmdId, decodedPacket.Body.ToArray(),Framing);
                        await HandlePacket(decodedPacket.CmdId, decodedPacket.Body.ToArray());
                        break;

                    case PacketFraming.Control:
                        Logger.Info("Control packet received");
                        // Handle control packet if needed
                        break;

                    case PacketFraming.Unknown:
                        Logger.Warn("Unknown packet format received");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Info("ReceiveLoop cancelled");
        }
        catch (Exception ex)
        {
            Logger.Info($"ReceiveLoop error: {ex}");
        }
        finally
        {
            Stop();
        }
        Stop();
    }

    private async Task HandlePacket(ushort opcode, byte[] payload)
    {
        var packetName = LogMap.GetValueOrDefault(opcode);
        if (DummyPacketNames.Contains(packetName!))
        {
            await SendDummy(packetName!);
            Logger.Info($"[Dummy] Send Dummy {packetName}");
            return;
        }

        // Before login only the login packets are accepted; nothing else has a player to act on.
        if (Player == null && opcode is not (CmdIds.ReqLogin or CmdIds.ReqReconnect))
            return;

        // Find the Handler for this opcode
        var handler = HandlerManager.GetHandler(opcode);
        if (handler != null)
        {
            // Handle
            // Make sure session is ready for packets
            var state = State;
            try
            {
                await handler.OnHandle(this, payload, (ushort)DownStreamSeqNo);
            }
            catch (Exception e)
            {
                Logger.Error(e.Message, e);
            }
            return;
        }

        if (ConfigManager.Config.ServerOption.EnableDebug &&
                 ConfigManager.Config.ServerOption.DebugNoHandlerPacket && !IgnoreLog.Contains(opcode))
            Logger.Error($"No handler found for {packetName}({opcode})");

        //if (ConfigManager.Config.ServerOption.AutoSendResponseWhenNoHandler)
        //{
        //    await SendDummy(packetName);
        //}

    }

    private async Task SendDummy(string packetName)
    {
        var respName = packetName.Replace("Req", "Rsp"); // Get the response packet name
        if (respName == packetName) return; // do not send rsp when resp name = recv name
        var respOpcode = LogMap.FirstOrDefault(x => x.Value == respName).Key; // Get the response opcode

        // Send Rsp
        await SendPacket(respOpcode);
    }
}
