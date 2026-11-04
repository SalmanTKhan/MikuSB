using System.Net.Sockets;
using System.Net;
using MikuSB.TcpSharp;
using MikuSB.Util;
using MikuSB.Database;
using MikuSB.Database.Account;

// Isolated SQLite round trip: no server/user database is touched.
var temp = Path.Combine(Path.GetTempPath(), "MikuSB-check-" + Guid.NewGuid().ToString("N"));
ConfigManager.Config.Path.DatabasePath = temp;
var database = new DatabaseHelper();
try
{
    database.Initialize();
    var account = new AccountData { Uid = 91234, Username = "foundation-check", Password = "synthetic" };
    DatabaseHelper.CreateInstance(account);
    var loaded = DatabaseHelper.sqlSugarScope!.Queryable<AccountData>().Single(x => x.Uid == account.Uid);
    if (loaded.Username != account.Username) throw new Exception("SQLite round trip failed");
    var player = new MikuSB.Database.Player.PlayerGameData { Uid = account.Uid, CompleteAllQuestLevels = true };
    DatabaseHelper.CreateInstance(player);
    DatabaseHelper.SaveCompleteAllQuestLevels(player);
    if (!DatabaseHelper.LoadCompleteAllQuestLevels(account.Uid)) throw new Exception("Quest flag round trip failed");
    Console.WriteLine("PASS SQLite account and quest flag round trips");
    MikuSB.Data.GameData.ChapterLevelData[10104] = new MikuSB.Data.Excel.ChapterLevelExcel { ID = 10104 };
    MikuSB.Data.GameData.ChapterLevelData[50174] = new MikuSB.Data.Excel.ChapterLevelExcel
    {
        ID = 50174,
        ExtraData = new Dictionary<string, Newtonsoft.Json.Linq.JToken> { ["Type"] = new Newtonsoft.Json.Linq.JValue(7) }
    };
    MikuSB.Data.GameData.DailyLevelData[20101] = new MikuSB.Data.Excel.DailyLevelExcel { ID = 20101 };
    var instance = new MikuSB.GameServer.Game.Player.PlayerInstance(player);
    await instance.OnEnterGame();
    instance.Attributes.Set(21, 10104, 1);
    var inventoryCount = instance.InventoryManager.InventoryData.Items.Count;
    var snapshot = instance.ToPlayerProto();
    foreach (var (level, expected) in new (uint, uint)[] { (10104, 7), (50174, 256), (20101, 263) })
    {
        if (snapshot.Attrs[instance.Attributes.ToShiftedAttrKey(21, level)] != expected ||
            snapshot.Attrs[instance.Attributes.ToPackedAttrKey(22, level)] != 1)
            throw new Exception("Testing progression missing from login snapshot");
    }
    if (instance.Attributes.GetValue(21, 10104) != 1 || instance.Attributes.Get(22, 50174) != null)
        throw new Exception("Testing overlay overwrote earned progression");
    await instance.QuestManager.SetAllLevelsForTestingAsync(false);
    snapshot = instance.ToPlayerProto();
    if (snapshot.Attrs[instance.Attributes.ToShiftedAttrKey(21, 10104)] != 1 ||
        snapshot.Attrs.ContainsKey(instance.Attributes.ToShiftedAttrKey(22, 50174)) ||
        DatabaseHelper.LoadCompleteAllQuestLevels(account.Uid))
        throw new Exception("Disabling progression test mode did not restore stored state");
    if (instance.InventoryManager.InventoryData.Items.Count != inventoryCount)
        throw new Exception("Progression overlay granted rewards");
    Console.WriteLine("PASS initial login progression overlay, stored-state preservation and reward-free reversal");

}
finally
{
    DatabaseHelper.Stop();
    await DatabaseHelper.WaitAsync();
    DatabaseHelper.sqlSugarScope?.Dispose();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(temp, true);
}

ConfigManager.Config.GameServer.BindAddress = "127.0.0.1";
ConfigManager.Config.GameServer.Port = 0;
SocketListener.StartListener<EchoConnection>();
try
{
    using var client = new TcpClient();
    await client.ConnectAsync(SocketListener.LocalEndPoint!);
    var stream = client.GetStream();
    var bytes = new PacketCodec().Encode(5, [8, 1]);
    await stream.WriteAsync(bytes.AsMemory(0, 3));
    await Task.Delay(20);
    await stream.WriteAsync(bytes.AsMemory(3));
    var reply = new byte[bytes.Length];
    await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    if (!bytes.SequenceEqual(reply)) throw new Exception("Fragmented transport round trip failed");
    var large = new byte[4 * 1024 * 1024];
    await stream.ReadExactlyAsync(large).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    for (var i = 0; i < large.Length; i++)
        if (large[i] != (byte)(i % 251)) throw new Exception("Large queued send corrupted");
    client.Close();
    for (var i = 0; i < 100 && !SocketListener.Connections.IsEmpty; i++) await Task.Delay(10);
    if (!SocketListener.Connections.IsEmpty) throw new Exception("Disconnected session leaked");
    Console.WriteLine("PASS Yggdrasil fragmented frame, send completion, disconnect cleanup");
}
finally { SocketListener.StopListener(); }
// A delayed abort callback previously crashed the process after Stop().
await Task.Delay(300);
SocketListener.StartListener<EchoConnection>();
SocketListener.StopListener();
await Task.Delay(300);
Console.WriteLine("PASS large queued send, idle listener shutdown and restart");

public class EchoConnection : SocketConnection
{
    public override async void Start()
    {
        try
        {
            if (!SocketListener.Connections.Values.Contains(this)) throw new Exception("Start before registration");
            using var stream = IncomingStream;
            var packet = await new PacketCodec().ReadPacketAsync(stream, CancelToken.Token)
                ?? throw new Exception("Missing packet");
            await SendPacket(new PacketCodec().Encode(packet.CmdId, packet.Body));
            var large = new byte[4 * 1024 * 1024];
            for (var i = 0; i < large.Length; i++) large[i] = (byte)(i % 251);
            await SendPacket(large);
            var one = new byte[1];
            await stream.ReadExactlyAsync(one, CancelToken.Token);
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        finally { Stop(); }
    }
}
