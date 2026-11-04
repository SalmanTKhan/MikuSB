using System.Text.Json.Serialization;
using MikuSB.GameServer.Game.Quest;

namespace MikuSB.GameServer.Server.CallGS.Handlers.Chapter;

// Chapter.lua Req_LevelFail: {nID, tbLog} after a defeat or retreat. The script registers no callback, so the reply is only an acknowledgement.
// The run's session closes: its seed can no longer settle. Vigor is not charged (MikuSB charges none on entry).
[CallGSApi("Chapter_LevelFail")]
public class Chapter_LevelFail : CallGSHandler<ChapterLevelFailParam>
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, ChapterLevelFailParam request)
    {
        if (request.LevelId == 0 || !context.Player.IsActiveChapterLevel(request.LevelId))
            return Task.FromResult(CallGSResult.Error("error.BadParam"));
        context.Player.EndLevelSession();
        return Task.FromResult(CallGSResult.Ok(new System.Text.Json.Nodes.JsonObject { ["nID"] = request.LevelId }));
    }
}

public sealed class ChapterLevelFailParam
{
    [JsonPropertyName("nID")] public uint LevelId { get; set; }
}
