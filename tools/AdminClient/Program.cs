using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: AdminClient \"quest complete_all on @1\"");
    return 1;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var socket = new ClientWebSocket();
// Only the fixed loopback emulator endpoint; no machine trust-store changes.
socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
await socket.ConnectAsync(new Uri("wss://127.0.0.1:21500/ws/gui-console"), timeout.Token);
var id = Guid.NewGuid().ToString();
var request = JsonSerializer.SerializeToUtf8Bytes(new { type = "execute_command", requestId = id, command = string.Join(' ', args) });
await socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token);
var buffer = new byte[8192];
while (true)
{
    using var message = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Admin connection closed.");
        message.Write(buffer, 0, result.Count);
        if (message.Length > 1024 * 1024) throw new IOException("Admin message too large.");
    } while (!result.EndOfMessage);
    using var json = JsonDocument.Parse(message.ToArray());
    var root = json.RootElement;
    var type = root.GetProperty("type").GetString();
    if (type == "packet_log") continue;
    if (root.TryGetProperty("requestId", out var requestId) && requestId.GetString() != id) continue;
    Console.WriteLine(root.GetProperty("message").GetString());
    return type == "command_result" && root.GetProperty("success").GetBoolean() ? 0 : 1;
}
