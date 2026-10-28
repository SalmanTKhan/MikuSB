using System.IO.Pipelines;
using Yggdrasil.Network.TCP;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using MikuSB.Enums.Packet;
using MikuSB.Proto;
using MikuSB.Util;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace MikuSB.TcpSharp;

public class SocketConnection : TcpConnection
{
    public static readonly ConcurrentBag<int> BannedPackets = [];
    private static readonly Logger Logger = new("GameServer");
    public static readonly ConcurrentDictionary<int, string> LogMap = [];

    public static readonly ConcurrentBag<int> IgnoreLog =
    [
        
    ];
    protected readonly CancellationTokenSource CancelToken = new();
    private readonly Pipe incoming = new(new PipeOptions(pauseWriterThreshold: 2 * 1024 * 1024, resumeWriterThreshold: 1024 * 1024));
    protected Stream IncomingStream => incoming.Reader.AsStream();
    public IPEndPoint RemoteEndPoint => IPEndPoint.Parse(Address);
    public string DebugFile = "";
    public bool IsOnline = true;
    public StreamWriter? Writer;
    public int DownStreamSeqNo;
    public int UpStreamSeqNo;
    public PacketFraming Framing;
    public SessionStateEnum State { get; set; } = SessionStateEnum.INACTIVE;
    internal long ConnectionId { get; set; }
    private int stopped;

    protected override void OnConnected() => Start();
    protected override void ReceiveData(byte[] buffer, int length)
    {
        // Copy before Yggdrasil reuses its receive buffer. Backpressure bounds queued bytes.
        incoming.Writer.WriteAsync(buffer.AsMemory(0, length), CancelToken.Token).AsTask().GetAwaiter().GetResult();
    }
    protected override void OnClosed(ConnectionCloseType type) => Stop();
    public virtual void Start()
    {
        Logger.Info($"New connection from {RemoteEndPoint}.");
        State = SessionStateEnum.WAITING_FOR_TOKEN;
    }
    public virtual void Stop(bool isServerStop = false)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        IsOnline = false;
        State = SessionStateEnum.INACTIVE;
        CancelToken.Cancel();
        incoming.Writer.CancelPendingFlush();
        Close();
        SocketListener.UnregisterConnection(this);
    }
    public bool SocketConnected() => Status == ConnectionStatus.Open;

    public void LogPacket(string sendOrRecv, ushort opcode, byte[] payload, PacketFraming framing)
    {
        if (!ConfigManager.Config.ServerOption.EnableDebug) return;
        try
        {
            //Logger.DebugWriteLine($"{sendOrRecv}: {Enum.GetName(typeof(OpCode), opcode)}({opcode})\r\n{Convert.ToHexString(payload)}");
            if (IgnoreLog.Contains(opcode)) return;
            if (!ConfigManager.Config.ServerOption.DebugDetailMessage) throw new Exception(); // go to catch block
            var typ = AppDomain.CurrentDomain.GetAssemblies()
                .SingleOrDefault(assembly => assembly.GetName().Name == "MikuProto")!.GetTypes()
                .First(t => t.Name == $"{LogMap[opcode]}"); //get the type using the packet name
            var descriptor =
                typ.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(
                    null, null) as MessageDescriptor; // get the static property Descriptor
            var packet = descriptor?.Parser.ParseFrom(payload);
            var formatter = JsonFormatter.Default;
            var asJson = formatter.Format(packet);
            var output = $"{sendOrRecv}: {LogMap[opcode]}({opcode}) ({framing})\r\n{asJson}";
            if (ConfigManager.Config.ServerOption.DebugMessage)
                Logger.Debug(output);
            InGameConsoleBridge.PublishPacketLog(output);
            if (DebugFile == "" || !ConfigManager.Config.ServerOption.SavePersonalDebugFile) return;
            var sw = GetWriter();
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss}] [GameServer] [DEBUG] " + output);
            sw.Flush();
        }
        catch
        {
            var output = $"{sendOrRecv}: {LogMap.GetValueOrDefault(opcode, "UnknownPacket")}({opcode})";
            if (ConfigManager.Config.ServerOption.DebugMessage)
                Logger.Debug(output);
            InGameConsoleBridge.PublishPacketLog(output);
            if (DebugFile != "" && ConfigManager.Config.ServerOption.SavePersonalDebugFile)
            {
                var sw = GetWriter();
                sw.WriteLine($"[{DateTime.Now:HH:mm:ss}] [GameServer] [DEBUG] " + output);
                sw.Flush();
            }
        }
    }

    private StreamWriter GetWriter()
    {
        // Create the file if it doesn't exist
        var file = new FileInfo(DebugFile);
        if (!file.Exists)
        {
            Directory.CreateDirectory(file.DirectoryName!);
            File.Create(DebugFile).Dispose();
        }

        Writer ??= new StreamWriter(DebugFile, true);
        return Writer;
    }

    public Task SendPacket(byte[] packet)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(packet, packet.Length, (_, _, result) =>
        {
            if (result == PostSendType.Sent) completion.TrySetResult();
            else completion.TrySetException(new IOException("Connection closed before send completed."));
        });
        return completion.Task;
    }

    public async Task SendPacket(BasePacket packet, ushort seqNo = 0)
    {
        // Test
        if (packet.CmdId <= 0)
        {
            Logger.Debug("Tried to send packet with missing cmd id!");
            return;
        }

        // DO NOT REMOVE (unless we find a way to validate code before sending to client which I don't think we can)
        if (BannedPackets.Contains(packet.CmdId)) return;
        LogPacket("Send", packet.CmdId, packet.Body,Framing);
        byte[] packetBytes = new PacketCodec().Encode(packet.CmdId, packet.Body,Framing);
        try
        {
            await SendPacket(packetBytes);
        }
        catch
        {
            // ignore
        }
    }

    public async Task SendPacket(int cmdId)
    {
        await SendPacket(new BasePacket((ushort)cmdId));
    }

    public async Task SendPacket(int cmdId, ushort seqNo)
    {
        var packet = new BasePacket((ushort)cmdId);
        packet.SeqNo = seqNo;
        await SendPacket(packet);
    }

    public async Task SendPacket(int cmdId, IMessage msg, ushort seqNo = 0)
    {
        var packet = new BasePacket((ushort)cmdId);
        packet.SetData(msg);
        packet.SeqNo = seqNo;
        await SendPacket(packet);
    }
}