using System.Reflection;
using System.Text.Json;

namespace MikuSB.GameServer.Server.CallGS;

public abstract class CallGSHandler : ICallGSHandler
{
    protected CallGSHandler()
    {
        Api = GetType().GetCustomAttribute<CallGSApiAttribute>()?.Api
            ?? throw new InvalidOperationException($"CallGSApiAttribute is missing on {GetType().Name}.");
    }

    protected string Api { get; }

    protected abstract Task<CallGSResult> HandleAsync(CallGSContext context, string param);

    public async Task Handle(Connection connection, string param, ushort seqNo)
    {
        var result = await HandleAsync(new CallGSContext
        {
            Connection = connection,
            Player = connection.Player!,
            SequenceNumber = seqNo,
            RawParam = param
        }, param);
        await SendResultAsync(connection, result);
    }

    protected static TRequest? Deserialize<TRequest>(string param)
    {
        return JsonSerializer.Deserialize<TRequest>(param, CallGSJson.Options);
    }

    protected static CallGSResult BadParam()
    {
        return CallGSResult.Error("error.BadParam");
    }

    private Task SendResultAsync(Connection connection, CallGSResult result)
    {
        if (!result.SendResponse)
            return Task.CompletedTask;

        var sync = connection.Player?.AchievementManager?.AfterCall(Api, result.Sync) ?? result.Sync;
        return CallGSRouter.SendScript(connection, result.Api ?? Api, result.Argument, sync!);
    }
}

public abstract class CallGSHandler<TRequest> : CallGSHandler where TRequest : class
{
    protected sealed override Task<CallGSResult> HandleAsync(CallGSContext context, string param)
    {
        var request = Deserialize<TRequest>(param);
        if (request == null)
            return Task.FromResult(BadParam());

        return HandleAsync(context, request);
    }

    protected abstract Task<CallGSResult> HandleAsync(CallGSContext context, TRequest request);
}
