namespace MikuSB.GameServer.Server.CallGS.Handlers.Captured;

public abstract class CapturedHandler : CallGSHandler
{
    protected override Task<CallGSResult> HandleAsync(CallGSContext context, string param) =>
        CapturedGameplay.HandleAsync(context, Api, param);
}
[CallGSApi("Chapter_NewPrologueEnterLevel")] public sealed class Chapter_NewPrologueEnterLevel : CapturedHandler { }
[CallGSApi("Chapter_NewPrologueSettlement")] public sealed class Chapter_NewPrologueSettlement : CapturedHandler { }
[CallGSApi("GuideLogic_WriteGuideLog")] public sealed class GuideLogic_WriteGuideLog : CapturedHandler { }
[CallGSApi("GuideLogic_SetBeginner")] public sealed class GuideLogic_SetBeginner : CapturedHandler { }
[CallGSApi("Launch_OnCloseLoadingLog")] public sealed class Launch_OnCloseLoadingLog : CapturedHandler { }
[CallGSApi("FavMailLog")] public sealed class FavMailLog : CapturedHandler { }
[CallGSApi("Online_ClientLog")] public sealed class Online_ClientLog : CapturedHandler { }
[CallGSApi("OnlineAssaultOpsBattlePass_ChangeBP")] public sealed class OnlineAssaultOpsBattlePass_ChangeBP : CapturedHandler { }
[CallGSApi("LimitedTime_CheckLimitedRoleGet")] public sealed class LimitedTime_CheckLimitedRoleGet : CapturedHandler { }
[CallGSApi("Activity_GetAward")] public sealed class Activity_GetAward : CapturedHandler { }
[CallGSApi("Activity_QuickQuestAward")] public sealed class Activity_QuickQuestAward : CapturedHandler { }
[CallGSApi("Activity_Flag")] public sealed class Activity_Flag : CapturedHandler { }
[CallGSApi("DownloadMultMediaLog")] public sealed class DownloadMultMediaLog : CapturedHandler { }
[CallGSApi("PlayerSetting_SetBirthday")] public sealed class PlayerSetting_SetBirthday : CapturedHandler { }
