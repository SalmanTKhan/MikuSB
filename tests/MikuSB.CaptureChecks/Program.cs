using System.Reflection;
using System.Text.Json.Nodes;
using MikuSB.Data;
using MikuSB.Data.Excel;
using MikuSB.Database;
using MikuSB.Database.Account;
using MikuSB.Database.Player;
using MikuSB.GameServer.Game.Player;
using MikuSB.GameServer.Game.Quest;
using MikuSB.GameServer.Server;
using MikuSB.GameServer.Server.CallGS;
using MikuSB.GameServer.Server.CallGS.Handlers.Chapter;
using MikuSB.GameServer.Server.CallGS.Handlers.Girl;
using MikuSB.GameServer.Server.CallGS.Handlers.Shop;
using MikuSB.GameServer.Server.CallGS.Handlers.BattlePass;
using MikuSB.Util;
using Newtonsoft.Json.Linq;

void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
bool Error(CallGSResult result) => JsonNode.Parse(result.Argument) is JsonObject o && (o["sErr"] != null || o["tbParam"]?["sErr"] != null);
var temp = Path.Combine(Path.GetTempPath(), "MikuSB-capture-" + Guid.NewGuid().ToString("N"));
ConfigManager.Config.Path.DatabasePath = temp;
ConfigManager.Config.Path.ResourcePath = Path.Combine(temp, "resources");
void Resource(string path, string value) {
    var file = Path.Combine(ConfigManager.Config.Path.ResourcePath, path);
    Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value);
}
Resource("activity/activities.json", """[{"Id":1,"Class":"signin_monthly"},{"Id":22,"Class":"activity_quest","Normal":[9001,9101,9999]},{"Id":30,"Class":"activity_quest","StartTime":"[209901010000]","Normal":[9101]},{"Id":31,"Class":"activity_quest","Condition":[[[15,10230]],[[2,10124]]],"Normal":[9101]},{"Id":32,"Class":"activity_quest","EndTime":"[202001010000]","Normal":[9101]},{"Id":33,"Class":"activity_quest","Condition":{"[26]":[2,50101]},"Normal":[9101]}]""");
Resource("activity/sign/signday.json", """[{"ActivityId":1,"DayId":1,"SignReward":[[4,3,1,1,2000]]}]""");
Resource("achievement/activity_quest.json", """[{"ID":9001,"Type":350,"CheckValue":1,"Param":[1,[10124]],"Rewards":[[4,3,1,1,10]]},{"ID":9101,"Type":187,"CheckValue":5,"Rewards":[[4,3,1,1,20]]},{"ID":9999,"Type":117,"CheckValue":5,"Rewards":[[4,3,1,1,100]]}]""");
var db = new DatabaseHelper(); db.Initialize();
try {
    var account = new AccountData { Uid = 77123, Username = "capture-check", Password = "synthetic" };
    DatabaseHelper.CreateInstance(account);
    var data = new PlayerGameData { Uid = account.Uid, Level = 5 };
    DatabaseHelper.CreateInstance(data);
    var player = new PlayerInstance(data); await player.OnEnterGame();
    var connection = new Connection { Player = player };
    CallGSContext Context(string raw) => new() { Connection=connection, Player=player, SequenceNumber=1, RawParam=raw };
    Task<CallGSResult> Call(string api, string raw) => CapturedGameplay.HandleAsync(Context(raw), api, raw);
    async Task<CallGSResult> Existing<T>(string raw) where T: CallGSHandler, new() =>
        await (Task<CallGSResult>)typeof(CallGSHandler).GetMethod("HandleAsync", BindingFlags.Instance|BindingFlags.NonPublic,
            null, [typeof(CallGSContext),typeof(string)],null)!.Invoke(new T(), [Context(raw),raw])!;

    var registered = typeof(CallGSRouter).Assembly.GetTypes().Select(t => t.GetCustomAttribute<CallGSApiAttribute>()?.Api).Where(a => a != null).ToHashSet();
    string[] observed = ["LanguageChange","OnlineAssaultOpsBattlePass_ChangeBP","ReqEntranceGreenLevel","BattlePassLogic_ClientRefresh","Daily_GetActivityInfo","Chapter_NewPrologueEnterLevel","Launch_OnCloseLoadingLog","FavMailLog","GuideLogic_WriteGuideLog","Chapter_SyncGuideLevelPassData","Chapter_NewPrologueSettlement","ExtendFightLog","ExtendFightDynamicLog","SettingChange","Chapter_DealLevelSettlement","Activity_Refresh","GuideLogic_SetBeginner","Activity_GetAward","ActivityFace_Update","LimitedTime_CheckLimitedRoleGet","Gacha_Launch","Chapter_EnterLevel","Lineup_Update","GirlCard_UpdateLevel","Activity_Flag","House_Request","Activity_QuickQuestAward","Online_ClientLog","IBLogic_GoodsRedDot"];
    Check(observed.All(registered.Contains), "Captured API registration gap");
    Check(registered.Count == typeof(CallGSRouter).Assembly.GetTypes().Count(t => t.GetCustomAttribute<CallGSApiAttribute>() != null), "Duplicate API registration");
    Console.WriteLine("PASS all 29 captured API names registered without duplicates");

    foreach (var id in new uint[] {10121,10124,50174,11421})
        GameData.ChapterLevelData[id] = new ChapterLevelExcel { ID=id,
            ExtraData = new Dictionary<string,JToken> { ["Type"] = new JValue(id==50174 ? 7 : 1) } };
    Check(Error(await Call("Chapter_NewPrologueSettlement", """{"nID":10121}""")), "Unentered prologue accepted");
    var entry = await Call("Chapter_NewPrologueEnterLevel", """{"nID":10121,"nTeamID":9,"nMultiple":1,"bFree":false}""");
    Check(JsonNode.Parse(entry.Argument)?["nSeed"]?.GetValue<uint>() > 0, "Missing seed");
    Check(player.QuestManager.SyncGuideLevelPassData(JsonNode.Parse("""{"tbData":[[10121,1]]}""")), "Active guide sync rejected");
    Check(!player.QuestManager.SyncGuideLevelPassData(JsonNode.Parse("""{"tbData":[[10124,99]]}""")), "Arbitrary guide pass accepted");
    var settled = await Call("Chapter_NewPrologueSettlement", """{"nID":10121,"nSeed":0,"bWaitServer":false}""");
    Check(!Error(settled) && JsonNode.Parse(settled.Argument)?["nID"]?.GetValue<uint>()==10121, "Direct prologue failed");
    Check(!Error(await Call("Chapter_NewPrologueSettlement", """{"nID":10121,"nSeed":0}""")), "Checkpoint retry failed");
    await Call("Chapter_NewPrologueEnterLevel", """{"nID":50174,"nTeamID":1}""");
    var plot = await Existing<Chapter_DealLevelSettlement>("""{"sCmd":"Chapter_NewPrologueSettlement","tbParam":{"nID":50174,"nSeed":0,"bWaitServer":true}}""");
    Check(JsonNode.Parse(plot.Argument)?["tbParam"]?["nID"]?.GetValue<uint>()==50174, "Wrapped plot shape mismatch");
    Check(player.Attributes.GetValue(21,50174)==256, "Plot completion not stored");
    Console.WriteLine("PASS direct/wrapped prologue, zero-seed checkpoint, retry and forged guide rejection");

    var beginner = await Call("GuideLogic_SetBeginner", """{"nType":1}""");
    Check(beginner.Argument=="null" && beginner.Sync!.Custom[12255233]==1, "Beginner callback mismatch");
    var bp = await Call("OnlineAssaultOpsBattlePass_ChangeBP", """{"nId":1}""");
    Check(bp.Sync!.Custom[12058625]==1, "Battle pass custom field mismatch");
    var log = await Call("GuideLogic_WriteGuideLog", """{"logstr":"31|1|NonForce|0"}""");
    Check(JsonNode.Parse(log.Argument)?["logstr"]?.GetValue<string>()=="31|1|NonForce|0", "Guide log echo mismatch");
    Check(!(await Call("Online_ClientLog", """{"GiftboxSuggest":[1,1,2,5,0]}""")).SendResponse, "Telemetry should only receive transport ACK");
    Console.WriteLine("PASS retail guide/battle-pass callback and telemetry behavior");

    var silverBox = new OtherItemExcel { Genre=4, Detail=3, Particular=1, Level=1, LuaType="silver_box", Param1Raw=new JValue(1) };
    GameData.OtherItemData.Remove(silverBox.TemplateId);
    var silver = player.Attributes.GetValue(1,7);
    // Missing template must reject before any grant; the follow-up claim proves no claim key was stored.
    Check(Error(await Call("Activity_GetAward", """{"nId":1,"nType":1}""")), "Ungrantable sign reward accepted");
    Check(player.Attributes.GetValue(1,7)==silver, "Rejected claim changed balance");
    GameData.OtherItemData[silverBox.TemplateId] = silverBox;
    var sign = await Call("Activity_GetAward", """{"nId":1,"nType":1}""");
    Check(sign.Api=="DaySign" && !Error(sign), "Sign claim failed");
    Check(player.Attributes.GetValue(1,7)==silver+2000, "Sign currency grant failed");
    Check(Error(await Call("Activity_GetAward", """{"nId":1,"nType":1}""")), "Duplicate sign granted");
    Check(Error(await Call("Activity_GetAward", """{"nId":22,"nQuestId":9001}""")), "Unfinished quest granted");
    foreach (var closedId in new[] {30,31,32,33})
        Check(Error(await Call("Activity_GetAward", $$"""{"nId":{{closedId}},"nQuestId":9101}""")), $"Closed activity {closedId} granted");
    player.Attributes.Set(22,10124,1);
    Check(!Error(await Call("Activity_GetAward", """{"nId":31,"nQuestId":9101}""")), "Condition alternative 2 not honoured");
    var claim = await Call("Activity_GetAward", """{"nId":22,"nQuestId":9001}""");
    Check(claim.Api=="ActivityQuest_GetReward" && !Error(claim), "Quest claim shape mismatch");
    var quick = await Call("Activity_QuickQuestAward", """{"nId":22,"tbIdList":[9101]}""");
    Check(quick.Api=="Activity_QuickQuestAward" && !Error(quick), "Quick quest claim failed");
    Check(Error(await Call("Activity_QuickQuestAward", """{"nId":22,"tbIdList":[9999]}""")), "Unresolved house eligibility granted");
    Check(player.Attributes.GetValue(1,7)==silver+2050, "Duplicate/invalid claim changed balance");
    var reload = DatabaseHelper.sqlSugarScope!.Queryable<PreservationProgressData>().Single(x=>x.Uid==data.Uid);
    Check(reload.Claims.Contains("quest:22:9001") && reload.Claims.Contains("quest:22:9101"), "Claims not durable");
    Console.WriteLine("PASS activity time/condition gating (unsupported clauses fail closed), sign/quest/quick rewards, duplicate and eligibility rejection, SQLite claim persistence");
    // Retail-shaped chapter flow from the capture: plot 51471 (nStar=-1) and combat 11421 (nStar=7), each
    // entered through Chapter_EnterLevel and settled with that seed. Drop rows mirror drop 1000/1026/1041.
    JArray Ids(params uint[] ids) => new(ids.Select(i => (object)i).ToArray());
    GameData.ChapterLevelData[51471] = new ChapterLevelExcel { ID=51471, ExtraData = new Dictionary<string,JToken> {
        ["Type"]=new JValue(7), ["FirstDropID"]=Ids(1000) } };
    GameData.ChapterLevelData[11421] = new ChapterLevelExcel { ID=11421, ExtraData = new Dictionary<string,JToken> {
        ["Type"]=new JValue(0), ["FirstDropID"]=Ids(1026), ["BaseDropID"]=Ids(1041) } };
    void DropFixture(uint id, string group) {
        GameData.DropData[id] = new DropExcel { ID=id, Drop=[[id,1,1]] };
        GameData.DropGroupData[id] = new DropGroupExcel { ID=id, RandTypeRaw=new JValue(""), GroupRaw=JArray.Parse(group) };
    }
    DropFixture(1000, "[[[4,3,2,1],10000,20,101]]");
    DropFixture(1026, "[[[4,3,2,1],10000,50,101],[[5,1,1,3],10000,2,203]]");
    DropFixture(1041, "[[[4,3,1,1],10000,1200,101],[[5,2,1,2],10000,1,203]]");
    foreach (var (d,p,l) in new[] {(3u,2u,1u),(3u,1u,1u)}) {
        var box = new OtherItemExcel { Genre=4, Detail=d, Particular=p, Level=l, LuaType=p==2 ? "money_box" : "silver_box", Param1Raw=new JValue(1) };
        GameData.OtherItemData[box.TemplateId] = box;
    }
    foreach (var (d,p,l,exp) in new[] {(1u,1u,3u,1000u),(2u,1u,2u,500u),(9u,9u,9u,(uint)int.MaxValue)}) {
        var supplies = new SuppliesExcel { Genre=5, Detail=d, Particular=p, Level=l, ProvideExpRaw=new JValue(exp), ConsumeGoldRaw=new JValue(10) };
        GameData.SuppliesData[supplies.TemplateId] = supplies;
    }
    for (var lv = 1; lv < 80; lv++) GameData.UpgradeExpData[lv] = new UpgradeExpExcel { Lv=lv, CardNeedExpRaw=new JValue(100) };
    JsonArray Settle(CallGSResult r) => JsonNode.Parse(r.Argument)!["tbParam"] as JsonArray ?? throw new Exception("Settlement error " + r.Argument);
    string SettleRaw(uint id, int star, uint seed) => $$$"""{"sCmd":"Chapter_LevelSettlement","tbParam":{"nTime":0,"nDrop":0,"tbMonster":[],"tbKill":[],"nSeed":{{{seed}}},"nID":{{{id}}},"nStar":{{{star}}}}}""";
    uint Enter(uint id, uint team) => JsonNode.Parse((Existing<Chapter_EnterLevel>($$"""{"nMultiple":1,"nTeamID":{{team}},"nID":{{id}}}""").Result).Argument)!["nSeed"]!.GetValue<uint>();

    Check(Error(await Existing<Chapter_DealLevelSettlement>(SettleRaw(51471,-1,12345))), "Unentered plot settled");
    var plotSeed = Enter(51471, 1);
    Check(Error(await Existing<Chapter_DealLevelSettlement>(SettleRaw(51471,-1,plotSeed+1))), "Plot settled with wrong seed");
    var plotRows = Settle(await Existing<Chapter_DealLevelSettlement>(SettleRaw(51471,-1,plotSeed)));
    Check(plotRows.ToJsonString()=="[[[4,3,2,1,20,1]]]", "Plot settlement shape differs from retail: " + plotRows.ToJsonString());

    var combatSeed = Enter(11421, 9);
    var combat = Settle(await Existing<Chapter_DealLevelSettlement>(SettleRaw(11421,7,combatSeed)));
    var rows = (combat.Single() as JsonArray)!.Select(r => (JsonArray)r!).ToList();
    string Gdplc(JsonArray r) => string.Join(',', r.Take(6).Select(n => n!.ToString()));
    Check(rows.Select(Gdplc).OrderBy(x=>x).SequenceEqual(new[] {"4,3,1,1,1200,2","4,3,2,1,50,1","5,1,1,3,2,1","5,2,1,2,1,2"}.OrderBy(x=>x)),
        "First clear must grant first (1) and base (2) drops like retail: " + combat.ToJsonString());
    var stackA = player.InventoryManager.InventoryData.Items.Values.Single(i => i.TemplateId==GameResourceTemplateId.FromGdpl(5,1,1,3));
    var stackB = player.InventoryManager.InventoryData.Items.Values.Single(i => i.TemplateId==GameResourceTemplateId.FromGdpl(5,2,1,2));
    Check(rows.Where(r => r[0]!.GetValue<int>()==5).All(r => r.Count==7) &&
          rows.Any(r => r.Count==7 && r[6]!.GetValue<int>()==(int)stackA.UniqueId), "Supplies rows must carry the stack id");
    Check(rows.Where(r => r[0]!.GetValue<int>()==4).All(r => r.Count==6), "Currency rows must not carry a stack id");
    Check(Error(await Existing<Chapter_DealLevelSettlement>(SettleRaw(11421,7,combatSeed+1))), "Combat settled with wrong seed");
    var repeatSeed = Enter(11421, 9);
    var repeat = Settle(await Existing<Chapter_DealLevelSettlement>(SettleRaw(11421,7,repeatSeed)));
    Check((repeat.Single() as JsonArray)!.All(r => r![5]!.GetValue<int>()==2), "Repeat clear granted first-clear drops");
    Console.WriteLine("PASS retail chapter settlement: plot/combat seed binding, first+base drops, row shape with supplies stack id");

    // GirlCard_UpdateLevel with the retail {Id, tbMaterials:[{Num,Id}]} shape, materials being supply stack ids.
    Resource("item/level_limit.json", """[{"ID":1001,"Type":1,"Limit":{"1":20,"19":20,"20":30,"100":80}}]""");
    GameData.CardData[990001] = new CardExcel { Genre=1, Detail=1, Particular=1, Level=1, Icon=990001, LevelLimitID=1001 };
    var card = new MikuSB.Database.Character.CharacterInfo { Guid=25, TemplateId=GameResourceTemplateId.FromGdpl(1,1,1,1), Level=1 };
    player.CharacterManager.CharacterData.Characters.Add(card);
    var silverBefore = player.Attributes.GetValue(1,7);
    var countA = stackA.ItemCount;
    Check(Error(await Existing<GirlCard_UpdateLevel>($$"""{"Id":25,"tbMaterials":[{"Num":{{countA+1}},"Id":{{stackA.UniqueId}}}]}""")), "Insufficient material accepted");
    var huge = player.InventoryManager.AddSuppliesItem(GameData.SuppliesData[GameResourceTemplateId.FromGdpl(5,9,9,9)], 2, false).Result!;
    Check(Error(await Existing<GirlCard_UpdateLevel>($$"""{"Id":25,"tbMaterials":[{"Num":2,"Id":{{huge.UniqueId}}}]}""")), "Exp overflow accepted");
    Check(huge.ItemCount==2 && stackA.ItemCount==countA && card.Level==1 && player.Attributes.GetValue(1,7)==silverBefore, "Rejected upgrade mutated state");
    var upgrade = await Existing<GirlCard_UpdateLevel>($$"""{"Id":25,"tbMaterials":[{"Num":2,"Id":{{stackA.UniqueId}}},{"Num":1,"Id":{{stackB.UniqueId}}}]}""");
    Check(upgrade.Argument=="null" && upgrade.Sync!.Items.Any(i => i.Count==0), "Upgrade response/sync differs from retail null + item sync");
    Check(card.Level==20 && player.Attributes.GetValue(1,7)==silverBefore-30, $"Upgrade result wrong level={card.Level} exp={card.Exp}");
    Console.WriteLine("PASS retail-shaped upgrade: level cap from configured level_limit, atomic rejection of overflow/insufficient input");

    var lineup = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Lineup.Lineup_Update>("""{"index":9,"member1":25,"member2":0,"member3":0,"name":"Retail"}""");
    Check(lineup.Argument=="null" && player.LineupManager.LineupData.LineupInfo[9].Name=="Retail" && player.LineupManager.LineupData.LineupInfo[9].Member1==25,
        "Retail lineup update not applied");
    Check(Error(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Lineup.Lineup_Update>("""{"index":9,"member1":25,"member2":25,"member3":0}""")), "Duplicate lineup member accepted");
    Check(await player.LineupManager.UpdateLineup(9,99999,0,0,false)==null, "Unowned lineup accepted");

    // Exact retail replies from the capture.
    var dailyIds = new[] {1,2,3,4,5,6,7,8,99};
    Resource("daily/daily.json", "[" + string.Join(',', dailyIds.Select(i => $$"""{"ID":{{i}},"Activity":[[20220331,20220431,3]]}""")) + "]");
    var retailDaily = "[" + string.Join(',', Enumerable.Range(1,99).Select(i => dailyIds.Contains(i) ? "[[20220331,20220431,3]]" : "null")) + "]";
    var daily = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Daily.Daily_GetActivityInfo>("null");
    Check(daily.Argument==retailDaily, "Daily_GetActivityInfo differs from retail: " + daily.Argument);
    var bpRefresh = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.BattlePass.BattlePassLogic_ClientRefresh>("null");
    Check(bpRefresh.Argument=="[]", "BattlePassLogic_ClientRefresh differs from retail: " + bpRefresh.Argument);
    var redDot = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.IBLogic_GoodsRedDot>("""{"tbList":[301]}""");
    Check(redDot.Argument=="""{"tbList":[301]}""", "IBLogic_GoodsRedDot differs from retail: " + redDot.Argument);
    Console.WriteLine("PASS retail replies for Daily_GetActivityInfo, BattlePassLogic_ClientRefresh, IBLogic_GoodsRedDot");

    // APIs first seen in the 2026-10-25 local session log (not in the retail capture).
    Check(!(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Misc.GirlRoomLog>("""{"sLog":"1-2-3-1|NULL"}""")).SendResponse, "GirlRoomLog should only ACK");
    Check((await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Online.OnlineAssaultOpsBattlePass_ClientRefresh>("null")).Argument=="[]", "Assault-ops BP refresh reply");
    var opcodes = typeof(CallGSRouter).Assembly.GetTypes()
        .Select(t => t.GetCustomAttribute<MikuSB.GameServer.Server.Packet.Opcode>()).Where(o => o != null).Select(o => o!.CmdId).ToList();
    Check(opcodes.Contains(MikuSB.Proto.CmdIds.ReqChangeWorldChannel), "ReqChangeWorldChannel has no packet handler");
    Console.WriteLine("PASS local-log gaps: GirlRoomLog ACK, assault-ops BP refresh, ReqChangeWorldChannel handler");

    Check(!(await Call("DownloadMultMediaLog", """{"tbLog":{"MailDownload":[1,"Picture.png",1]}}""")).SendResponse, "DownloadMultMediaLog should only ACK");
    var birthday = await Call("PlayerSetting_SetBirthday", """{"nDay":9,"nMouth":1}""");
    Check(birthday.Argument=="null" && birthday.Sync!.Custom[(151u<<16)|1]==(uint)DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Year,
        "Birthday reply must be null with custom 151/1 = current year");
    Check(Error(await Call("PlayerSetting_SetBirthday", """{"nDay":30,"nMouth":2}""")), "Invalid birthday accepted");
    Console.WriteLine("PASS retail DownloadMultMediaLog ACK and PlayerSetting_SetBirthday reply/custom attribute");

    // Shop: synthetic fixture for the two listing modes and persistence.
    Resource("shop/shop_tab.json", """[{"ShopId":12,"Begin":"[202109010000]","End":"[209909191400]","Columns":3,"RefreshTime":"[0400]"},{"ShopId":13,"Begin":"[202109010000]","End":"[209909191400]","Columns":100}]""");
    Resource("shop/goods.json", """[{"ShopId":12,"GoodsId":1201,"PosIndex":1,"Weight":10},{"ShopId":12,"GoodsId":1202,"PosIndex":1,"Weight":10},{"ShopId":12,"GoodsId":1210,"PosIndex":2,"Weight":10},{"ShopId":12,"GoodsId":1245,"PosIndex":"","Weight":100},{"ShopId":12,"GoodsId":1246,"PosIndex":"","Weight":50},{"ShopId":12,"GoodsId":1247,"PosIndex":"","Weight":25},{"ShopId":13,"GoodsId":1301,"PosIndex":1,"Weight":10},{"ShopId":13,"GoodsId":1302,"PosIndex":1,"Weight":10},{"ShopId":13,"GoodsId":1303,"PosIndex":5,"Weight":10}]""");
    JsonArray ShopIds(CallGSResult r) => new(JsonNode.Parse(r.Argument)!["goodsList"]!.AsArray().Select(g => (JsonNode?)g!["goodsid"]!.GetValue<uint>()).ToArray());
    var shop13 = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetGoodsList>("""{"nShopId":13}""");
    Check(shop13.Argument=="""{"shopId":13,"goodsList":[{"goodsid":1301,"discountindex":0},{"goodsid":1302,"discountindex":0},{"goodsid":1303,"discountindex":0}]}""",
        "Slotted shop must list every row (retail shop 50 kept shared-PosIndex rows): " + shop13.Argument);
    var shop12 = ShopIds(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetGoodsList>("""{"nShopId":12}""")).Select(n => n!.GetValue<uint>()).ToList();
    Check(shop12.Count==3 && shop12[0] is 1201 or 1202 && shop12[1]==1210 && shop12[2] is >= 1245 and <= 1247, "Random shop roll shape: " + string.Join(',', shop12));
    var again = ShopIds(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetGoodsList>("""{"nShopId":12}""")).Select(n => n!.GetValue<uint>()).ToList();
    Check(again.SequenceEqual(shop12), "Random shop list must persist inside its refresh window");
    var openTime = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetOpenTime>("null");
    Check(JsonNode.Parse(openTime.Argument)!.AsArray() is { Count: 13 } ot && ot[11]!["nBegin"]!.GetValue<long>()==1630425600 && ot[0]==null,
        "Open time must be indexed by ShopId with UTC+8 config times: " + openTime.Argument);
    Console.WriteLine("PASS shop goods list (slotted list-all, random roll persisted) and UTC+8 open times");

    // Shop against the real resource set and the retail replies, when the sibling resource checkout is present.
    var repoRoot = AppContext.BaseDirectory;
    while (repoRoot != null && !File.Exists(Path.Combine(repoRoot, "MikuSB.sln"))) repoRoot = Path.GetDirectoryName(repoRoot);
    var realResources = repoRoot == null ? null : Path.GetFullPath(Path.Combine(repoRoot, "..", "MikuSB-Resource"));
    if (realResources != null && File.Exists(Path.Combine(realResources, "shop", "goods.json"))) {
        var syntheticRoot = ConfigManager.Config.Path.ResourcePath;
        ConfigManager.Config.Path.ResourcePath = realResources;
        try {
            DatabaseHelper.GetInstanceOrCreateNew<PreservationProgressData>(player.Uid).Values.Remove("shop:12"); // drop the synthetic roll
            var retailShop = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "retail-shop-20261025.json")))!;
            foreach (var (shopId, retailList) in retailShop["goodsLists"]!.AsObject()) {
                var expected = retailList!["goodsList"]!.AsArray().Select(g => g!["goodsid"]!.GetValue<uint>()).ToHashSet();
                var actual = ShopIds(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetGoodsList>($$"""{"nShopId":{{shopId}}}""")).Select(n => n!.GetValue<uint>()).ToList();
                if (shopId == "12")
                    Check(actual.Count==expected.Count, $"Shop 12 roll size {actual.Count} != retail {expected.Count}");
                else
                    Check(actual.ToHashSet().SetEquals(expected), $"Shop {shopId} goods differ from retail");
            }
            var retailOpen = retailShop["openTime"]!.AsArray();
            var ours = MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetOpenTime.BuildResponse();
            int same = 0, newer = 0;
            for (var i = 0; i < retailOpen.Count; i++) {
                if (retailOpen[i] == null) { Check(i >= ours.Count || ours[i] == null, $"Shop {i+1} open but retail has none"); continue; }
                if (i < ours.Count && ours[i]?.ToJsonString() == retailOpen[i]!.ToJsonString()) same++; else newer++;
            }
            Check(same >= 67 && newer <= 9, $"Open time match {same}, differing {newer}");
            Console.WriteLine($"PASS real resources vs retail shop replies: 9 goods lists identical, shop 12 roll size, open times {same} identical / {newer} newer-than-resources");
        }
        finally { ConfigManager.Config.Path.ResourcePath = syntheticRoot; }
    }
    // Shop, mail, weapon, level sessions, battle pass: the handlers against synthetic tables (their own root: resource files are cached by path).
    ConfigManager.Config.Path.ResourcePath = Path.Combine(temp, "resources-online");
    Resource("shop/shop_tab.json", """[{"ShopId":70,"OnOff":1,"Columns":2,"Version":3,"ListVersion":1,"RefreshFee":[10,20],"RefreshLimits":2,"MoneyType":2,"RefreshTime":"[0400]"},{"ShopId":71,"OnOff":1,"Columns":5}]""");
    Resource("shop/goods.json", """[{"ShopId":70,"GoodsId":7001,"GDPLN":[4,3,1,1,5],"Weight":1,"Price1":[2,10]},{"ShopId":70,"GoodsId":7002,"GDPLN":[4,3,1,1,5],"Weight":1,"Price1":[2,10]},{"ShopId":70,"GoodsId":7003,"GDPLN":[4,3,1,1,5],"Weight":1,"Price1":[2,10]},{"ShopId":71,"GoodsId":7101,"GDPLN":[4,3,1,1,100],"PosIndex":1,"Calculation":1,"Price1":[2,10],"Price2":[5,1,1,3,1],"LimitType":1,"LimitNum":2},{"ShopId":71,"GoodsId":7102,"GDPLN":[4,3,1,1,1],"PosIndex":2,"End":"[202001010000]","Price1":[2,1]}]""");
    Resource("battlepass/timelist.json", """[{"ID":501,"StartTime":"[202001010000]","EndTime":"[209901010000]","ExpStep":1000,"MaxExPerWeek":1500,"Money":[2,200],"Daily":[5001,5002],"Weekly":[],"Normal":[],"NormalBox":[4,3,1,1,7],"AdvanceBox":[4,3,1,1,9]}]""");
    Resource("battlepass/levellist.json", """[{"ID":501,"Level":1,"NormalAward":[4,3,1,1,11],"AdvanceAward":[4,3,1,1,13]},{"ID":501,"Level":2,"NormalAward":[4,3,1,1,17],"AdvanceAward":[4,3,1,1,19]}]""");
    Resource("achievement/activity_quest.json", """[{"ID":5001,"Rewards":[[4,1,4,1,1200],[4,3,1,1,3]]},{"ID":5002,"Type":187,"CheckValue":999,"Rewards":[[4,1,4,1,100]]}]""");
    long Silver() => player.Attributes.GetValue(1, 7);
    string? Err(CallGSResult r) => JsonNode.Parse(r.Argument)?["sErr"]?.GetValue<string>();
    player.Attributes.Set(1, 5, 1000);   // gold
    var gold = player.Attributes.GetValue(1, 5);
    var list = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Shop.ShopLogic_GetGoodsList>("""{"nShopId":71}""");
    Check(list.Sync!.CustomStr.ContainsKey((1u << 16) | 71) && JsonNode.Parse(list.Sync.CustomStr[(1u << 16) | 71])?["tbgoods"]?.AsArray().Count == 2, "Shop attribute not synced");
    Check(Err(await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":9999,"nBuyMode":1,"nCount":1}""")) == "ui.TxtNotOpen", "Unknown goods sold");
    Check(Err(await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":7102,"nBuyMode":1,"nCount":1}""")) == "ui.TxtNotOpen", "Goods past their window sold");
    Check(Err(await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":7101,"nBuyMode":1,"nCount":100}""")) == "error.BadParam", "Over 99 at once accepted");
    Check(Err(await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":7101,"nBuyMode":1,"nCount":3}""")) == "ui.TxtLimitBuy", "Limit ignored");
    var before = Silver();
    var bought = await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":7101,"nBuyMode":1,"nCount":2}""");
    Check(Err(bought) == null && JsonNode.Parse(bought.Argument)?["tbGoods"]?[4]?.GetValue<int>() == 200, "Buy reply shape");
    Check(Silver() == before + 200 && player.Attributes.GetValue(1, 5) == gold - 20, "Buy did not charge and grant");
    Check(JsonNode.Parse(bought.Sync!.CustomStr[(1u << 16) | (1000 + 7101)])?["buynum"]?.GetValue<int>() == 2, "Bought count not synced");
    Check(Err(await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":7101,"nBuyMode":1,"nCount":1}""")) == "ui.TxtLimitBuy", "Limit not enforced after buying");
    player.Attributes.Set(1, 5, 5);
    Check(Err(await Existing<ShopLogic_BuyGoods>("""{"nGoodsId":7001,"nBuyMode":1,"nCount":1}""")) is "tip.gold_not_enough" or "ui.TxtNotOpen", "Bought without the money");
    Check(player.Attributes.GetValue(1, 5) == 5, "A refused buy charged");
    Check(Err(await Existing<ShopLogic_UpdateShop>("""{"nShopId":70}""")) == "tip.gold_not_enough", "Refresh without its fee");
    player.Attributes.Set(1, 5, 25);
    var refreshed = await Existing<ShopLogic_UpdateShop>("""{"nShopId":70}""");
    Check(Err(refreshed) == null && refreshed.Api == "ShopLogic_GetGoodsList" && player.Attributes.GetValue(1, 5) == 15, "First refresh fee");
    Check(Err(await Existing<ShopLogic_UpdateShop>("""{"nShopId":70}""")) == "tip.gold_not_enough", "Second refresh costs more");
    player.Attributes.Set(1, 5, 20);
    Check(Err(await Existing<ShopLogic_UpdateShop>("""{"nShopId":70}""")) == null, "Second refresh");
    Check(Err(await Existing<ShopLogic_UpdateShop>("""{"nShopId":70}""")) == "tip.refresh_run_out", "Refresh limit");
    Console.WriteLine("PASS shop: attribute sync, stock, window, per-buy cap, limit, charge and grant, refused buys charge nothing, refresh fees and limit");

    Check(player.MailManager.Data.WelcomeSent && player.MailManager.Data.Mails.Count == 1, "Welcome mail");
    var welcome = player.MailManager.Data.Mails[0];
    Check(player.ToPlayerProto().MailBox[welcome.Mid].Stat == MikuSB.Proto.MailStat.New, "Mail box in the snapshot");
    Check(player.MailManager.Read(welcome.Mid)?.Stat == 1, "Read");
    Check(player.MailManager.Delete(welcome.Mid) == null, "Deleted with its attachments unclaimed");
    var silverMail = Silver();
    var mailSync = new MikuSB.Proto.NtfSyncPlayer();
    GameData.OtherItemData[new OtherItemExcel { Genre=4, Detail=3, Particular=2, Level=1 }.TemplateId] = new OtherItemExcel { Genre=4, Detail=3, Particular=2, Level=1, LuaType="gold_box", Param1Raw=new JValue(1) };
    Check((await player.MailManager.Claim(welcome.Mid, mailSync))?.Stat == 2 && Silver() == silverMail + 100000, "Claim grants");
    Check(await player.MailManager.Claim(welcome.Mid, new MikuSB.Proto.NtfSyncPlayer()) == null && Silver() == silverMail + 100000, "Claim twice");
    Check(player.MailManager.Delete(welcome.Mid) != null && player.MailManager.Data.Mails.Count == 0, "Delete");
    Check(DatabaseHelper.sqlSugarScope!.Queryable<MikuSB.Database.Mail.MailData>().Single(x => x.Uid == data.Uid).WelcomeSent, "Mail state not durable");
    Console.WriteLine("PASS mail: welcome once, snapshot box, read, claim once, delete after claim, durable");

    GameData.CardData[990002] = new CardExcel { Genre=1, Detail=1, Particular=2, Level=1, Icon=990002, LevelLimitID=1001, DefaultWeaponGPDL=[2,7,1,1] };
    GameData.CardData[990003] = new CardExcel { Genre=1, Detail=1, Particular=3, Level=1, Icon=990003, LevelLimitID=1001, DefaultWeaponGPDL=[2,7,1,1] };
    foreach (var (wd,wp) in new[] {(7u,1u),(7u,2u),(5u,1u)}) GameData.WeaponData[wd*1000+wp+990000] = new WeaponExcel { Genre=2, Detail=wd, Particular=wp, Level=1 };
    var a = (await player.CharacterManager.AddCharacter(MikuSB.Enums.Item.ItemTypeEnum.TYPE_CARD, 1, 2, 1, sendPacket:false))!;
    var b = (await player.CharacterManager.AddCharacter(MikuSB.Enums.Item.ItemTypeEnum.TYPE_CARD, 1, 3, 1, sendPacket:false))!;
    var smgA = (await player.InventoryManager.AddWeaponItem(MikuSB.Enums.Item.ItemTypeEnum.TYPE_WEAPON, 7, 1, 1, sendPacket:false))!;
    var smgB = (await player.InventoryManager.AddWeaponItem(MikuSB.Enums.Item.ItemTypeEnum.TYPE_WEAPON, 7, 2, 1, sendPacket:false))!;
    var rifle = (await player.InventoryManager.AddWeaponItem(MikuSB.Enums.Item.ItemTypeEnum.TYPE_WEAPON, 5, 1, 1, sendPacket:false))!;
    Check(Err(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Weapon.Weapon_Replace>($$"""{"CardId":{{a.Guid}},"Id":{{rifle.UniqueId}}}""")) == "error.BadParam", "Wrong weapon type accepted");
    Check(Err(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Weapon.Weapon_Replace>($$"""{"CardId":{{a.Guid}},"Id":99999999}""")) == "error.BadParam", "Unknown weapon accepted");
    Check(Err(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Weapon.Weapon_Replace>($$"""{"CardId":{{a.Guid}},"Id":{{smgA.UniqueId}}}""")) == null && a.WeaponUniqueId == smgA.UniqueId, "Equip");
    Check(Err(await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Weapon.Weapon_Replace>($$"""{"CardId":{{b.Guid}},"Id":{{smgB.UniqueId}}}""")) == null, "Equip b");
    var swap = await Existing<MikuSB.GameServer.Server.CallGS.Handlers.Weapon.Weapon_Replace>($$"""{"CardId":{{b.Guid}},"Id":{{smgA.UniqueId}}}""");
    Check(Err(swap) == null && b.WeaponUniqueId == smgA.UniqueId && a.WeaponUniqueId == smgB.UniqueId && swap.Sync!.Items.Any(i => i.Id == a.Guid), "Holder not swapped");
    Console.WriteLine("PASS weapon replace: type check, swap with the previous holder, both cards synced");

    GameData.ChapterLevelData[11499] = new ChapterLevelExcel { ID=11499, ExtraData = new Dictionary<string,JToken> { ["Type"]=new JValue(0), ["FirstDropID"]=Ids(1026), ["BaseDropID"]=Ids(1041) } };
    var entered = await Existing<Chapter_EnterLevel>("""{"nID":11499,"nTeamID":1}""");
    var seed = JsonNode.Parse(entered.Argument)?["nSeed"]?.GetValue<uint>() ?? 0;
    // A new login on the same account finds the run: the session is stored with the player.
    var relogged = new PlayerInstance(data); await relogged.OnEnterGame();
    Check(relogged.IsLevelSession(MikuSB.GameServer.Game.Quest.QuestLevelType.Chapter, 11499, seed), "Session did not survive a new login");
    var connection2 = new Connection { Player = relogged };
    async Task<CallGSResult> On<T>(PlayerInstance who, Connection con, string raw) where T: CallGSHandler, new() =>
        await (Task<CallGSResult>)typeof(CallGSHandler).GetMethod("HandleAsync", BindingFlags.Instance|BindingFlags.NonPublic, null, [typeof(CallGSContext),typeof(string)],null)!
            .Invoke(new T(), [new CallGSContext { Connection=con, Player=who, SequenceNumber=1, RawParam=raw }, raw])!;
    var resumed = await On<Chapter_DealLevelSettlement>(relogged, connection2, $$$"""{"sCmd":"Chapter_LevelSettlement","tbParam":{"nID":11499,"nStar":7,"nSeed":{{{seed}}}}}""");
    Check(JsonNode.Parse(resumed.Argument)?["tbParam"] is JsonArray && relogged.Attributes.GetValue(22, 11499) == 1, "Settlement after a new login failed");
    var replay = await On<Chapter_DealLevelSettlement>(relogged, connection2, $$$"""{"sCmd":"Chapter_LevelSettlement","tbParam":{"nID":11499,"nStar":7,"nSeed":{{{seed}}}}}""");
    Check(JsonNode.Parse(replay.Argument)?["tbParam"] is JsonArray && relogged.Attributes.GetValue(22, 11499) == 1, "Repeated settlement granted twice");
    var failedRun = await On<Chapter_EnterLevel>(relogged, connection2, """{"nID":11499,"nTeamID":1}""");
    var failedSeed = JsonNode.Parse(failedRun.Argument)?["nSeed"]?.GetValue<uint>() ?? 0;
    Check(Err(await On<Chapter_LevelFail>(relogged, connection2, """{"nID":10121}""")) == "error.BadParam", "Failing another level closed the session");
    Check(Err(await On<Chapter_LevelFail>(relogged, connection2, """{"nID":11499,"tbLog":{}}""")) == null, "Level fail");
    Check(Error(await On<Chapter_DealLevelSettlement>(relogged, connection2, $$$"""{"sCmd":"Chapter_LevelSettlement","tbParam":{"nID":11499,"nStar":7,"nSeed":{{{failedSeed}}}}}""")) && relogged.Attributes.GetValue(22, 11499) == 1, "A failed run settled");
    var third = new PlayerInstance(data); await third.OnEnterGame();
    Check(!third.IsActiveChapterLevel(11499), "A failed run came back after a new login");
    Console.WriteLine("PASS level sessions: survive a new login, repeat settlement grants nothing, Chapter_LevelFail closes the run");

    GameData.BattlePassTimeData[501] = new BattlePassTimeExcel { Id=501, StartTime="[202001010000]", EndTime="[209901010000]", ExpStep=1000, MaxExPerWeek=1500 };
    foreach (var other in GameData.BattlePassTimeData.Keys.Where(k => k != 501).ToList()) GameData.BattlePassTimeData.Remove(other);
    var pass = new PlayerInstance(data); await pass.OnEnterGame();
    var passCon = new Connection { Player = pass };
    long PassSilver() => pass.Attributes.GetValue(1, 7);
    pass.Attributes.Set(1, 5, 1000);
    Check(Err(await On<BattlePassLogic_BuyLevel>(pass, passCon, """{"nLevel":1}""")) == "tip.BattlePass_NotGet_BP", "Levels bought without a pass");
    await On<BattlePassLogic_FirstOpen>(pass, passCon, "{}");
    Check(pass.Attributes.GetValue(25, 11) == 1 && pass.Attributes.GetValue(25, 1) == 501, "First open");
    var mission = await On<BattlePassLogic_QuickGetReward>(pass, passCon, """{"tbIdList":[[1,5001],[1,5002],[1,5001],[1,4242]]}""");
    Check(pass.Attributes.GetValue(25, 3) == 1200 && pass.Attributes.GetValue(25, 4) == 1200 && (pass.Attributes.GetValue(7, 5001) & 1) == 1 && (pass.Attributes.GetValue(7, 5002) & 1) == 0, "Mission XP");
    Check(JsonNode.Parse(mission.Argument)?["tbAwards"]?.AsArray().Count == 2, "Mission awards");
    await On<BattlePassLogic_QuickGetReward>(pass, passCon, """{"tbIdList":[[1,5001]]}""");
    Check(pass.Attributes.GetValue(25, 3) == 1200, "Mission claimed twice");
    pass.Attributes.Set(25, 3, 1500);
    var silverPass = PassSilver();
    var claimFree = await On<BattlePassLogic_GetPassAward>(pass, passCon, "{}");
    Check(PassSilver() == silverPass + 11 && pass.Attributes.GetValue(25, 100) == 1 && pass.Attributes.GetValue(25, 200) == 0, "Free level claim");
    Check(JsonNode.Parse(claimFree.Argument)?["tbAwards"]?.AsArray().Count == 1, "Award list");
    await On<BattlePassLogic_GetPassAward>(pass, passCon, "{}");
    Check(PassSilver() == silverPass + 11, "Level claimed twice");
    pass.Attributes.Set(25, 2, 2);
    var bought1 = await On<BattlePassLogic_BuyLevel>(pass, passCon, """{"nLevel":1}""");
    Check(Err(bought1) == null && pass.Attributes.GetValue(25, 3) == 2500 && pass.Attributes.GetValue(1, 5) == 800, "Buy level");
    Check(Err(await On<BattlePassLogic_BuyLevel>(pass, passCon, """{"nLevel":1}""")) == "tip.BattlePass_Level_Max", "Bought past the top");
    await On<BattlePassLogic_GetPassAward>(pass, passCon, "{}");
    Check(PassSilver() == silverPass + 11 + 17 + 13 + 19 && pass.Attributes.GetValue(25, 100) == 2 && pass.Attributes.GetValue(25, 200) == 2, "Premium catches up");
    pass.Attributes.Set(25, 3, 4000);
    await On<BattlePassLogic_GetPassAward>(pass, passCon, "{}");
    Check(PassSilver() == silverPass + 60 + 7 * 2 + 9 * 2, "Past the top gives the box per level");
    Console.WriteLine("PASS battle pass: first open, free and premium claims once each, buy level, top level and box");
    Check(Error(await Existing<GirlCard_UpdateLevel>("""{"Id":-1,"tbMaterials":[{"Id":1,"Num":1}]}""")), "Negative card GUID accepted");
    Console.WriteLine("PASS lineup ownership and invalid upgrade rejection");
}
finally {
    DatabaseHelper.Stop(); await DatabaseHelper.WaitAsync(); DatabaseHelper.sqlSugarScope?.Dispose();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(temp,true);
}
