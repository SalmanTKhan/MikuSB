using System.Text.Json.Nodes;
using MikuSB.Internationalization;
using MikuSB.Proto;
using MikuSB.Util;
using System.Reflection;

namespace MikuSB.GameServer.Server.CallGS;

public static class CallGSRouter
{
    private static readonly Logger Logger = new("CallGS");
    private static readonly Dictionary<string, ICallGSHandler> Handlers = [];
    private const string UnavailableTipKey = "ui.TxtNotOpen";

    public static void Init()
    {
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            var attr = type.GetCustomAttribute<CallGSApiAttribute>();
            if (attr == null) continue;
            Handlers[attr.Api] = (ICallGSHandler)Activator.CreateInstance(type)!;
        }
        Logger.Info(I18NManager.Translate("Server.ServerInfo.RegisterItem",
            Handlers.Count.ToString(), "CallGS"));
    }

    public static async Task Route(Connection connection, ReqCallGS req, ushort seqNo)
    {
        var player = connection.Player;
        if (player == null)
        {
            // The session is not fully logged in yet: the client reconnects as soon as the TCP
            // listener is up, which can be before the resource manager finished loading. Dispatching
            // would throw on the missing player, and answering would only confuse the client.
            Logger.Warn($"Dropping CallGS {req.Api} because the connection has no player yet.");
            return;
        }

        // Retail capture: every ReqCallGS receives an empty transport acknowledgment,
        // independently of whether a Lua callback is produced.
        await connection.SendPacket(CmdIds.RspCallGS);

        if (Handlers.TryGetValue(req.Api, out var handler))
        {
            try
            {
                // These also exist as plain APIs, which must not be a way around the seed check.
                if (SettlementSeeds.IsGuarded(req.Api))
                {
                    JsonNode? param = null;
                    try { param = JsonNode.Parse(req.Param); } catch (System.Text.Json.JsonException) { }
                    if (SettlementSeeds.Check(player.Uid, req.Api, param, out _) != SettlementSeeds.Outcome.Accept)
                    {
                        await SendUnavailableResponse(connection, req.Api);
                        return;
                    }

                    SettlementSeeds.Complete(player.Uid, req.Api, "{}");
                }

                await handler.Handle(connection, req.Param, seqNo);
                await player.OnHeartBeat();
            }
            catch (Exception e)
            {
                Logger.Error($"[{req.Api}] {e.Message}", e);
                await SendUnavailableResponse(connection, req.Api);
            }
            return;
        }

        Logger.Error($"No handler for CallGS API: {req.Api}");
        await SendUnavailableResponse(connection, req.Api);
    }

    public static async Task SendScript(Connection connection, string api, string arg, NtfSyncPlayer extra = null!)
    {
        var rsp = new NtfCallScript { Api = api, Arg = arg, ExtraSync = extra };
        await connection.SendPacket(CmdIds.NtfScript, rsp);
    }

    private static Task SendUnavailableResponse(Connection connection, string api)
    {
        // Many client Lua handlers treat sErr/sError as a recoverable failure path,
        // which is preferable to leaving the request hanging forever.
        return SendScript(connection, api, $$"""{"sErr":"{{UnavailableTipKey}}","sError":"{{UnavailableTipKey}}"}""");
    }
}
