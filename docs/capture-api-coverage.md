# Capture API coverage — official-server session 2026-10-25

Source: `E:/Projects/PyPacketViewWeb/captures/snowbreak/snowbreak-official-20261025-153700-691085` (normal Steam client against the original server, no MikuSB). The raw capture contains private session data and is not part of this repository.

This report separates four things that are easy to conflate:

- **Observed**: the request appears in the capture (count of `ReqCallGS`).
- **Registered**: a `[CallGSApi]` handler exists, so the router no longer drops the call.
- **Checked**: `tests/MikuSB.CaptureChecks` exercises the handler against a retail-shaped request and asserts on the result.
- **Retail match**: the emulator's response shape and side effects agree with what the original server sent. "Unverified" means nobody has compared them.

Registration is not gameplay acceptance. No row here has been accepted in a local client run yet.

## Totals and method

- 302 `ReqCallGS` records. 294 carry a decoded `api` field and give the 29 API names below; the other 8 were not attributed to an API.
- Request field sets come from the decoded `param` JSON (keys only).
- Callbacks were matched heuristically to the nearest preceding request on the gameplay flow. GuideLogic_WriteGuideLog requests (184) are interleaved with everything else, so asynchronous callbacks are sometimes attributed to the wrong request. The callback names below were checked by hand against the API that owns them.
- Router behaviour: every request gets an empty `RspCallGS` before dispatch (302 requests / 302 empty responses in retail). Header sequence correlation has not been independently verified.

## Status legend

| Status | Meaning |
|---|---|
| **Matched** | Checked, and the response shape/side effects agree with the capture |
| **Checked** | Exercised by CaptureChecks, but some retail detail is inferred |
| **Partial** | Works for the observed case; known gaps listed |
| **Mismatch** | Emulator response differs from the capture |
| **ACK only** | No Lua callback in retail; emulator sends nothing beyond the transport ACK |
| **Stub** | Registered, returns a placeholder, behaviour not implemented |

## Per-API table

| API | Observed | Retail callback (args) | Emulator handler | Status | Notes |
|---|---:|---|---|---|---|
| GuideLogic_WriteGuideLog | 184 | `GuideLogic_WriteGuideLog {logstr}` echo | Captured/CapturedHandlers | **Matched** | Echo checked; latest bounded log kept locally |
| House_Request | 17 | `House_Request {FuncName,...}` per sub-function | House/House_Request | **Partial** | 5 of 12 observed FuncNames handled; see House section |
| ExtendFightLog | 12 | none | Misc/ExtendFightLog | **ACK only** | Telemetry |
| ExtendFightDynamicLog | 12 | none | Misc/ExtendFightDynamicLog | **ACK only** | Telemetry |
| Chapter_NewPrologueEnterLevel | 8 | `{nSeed}` | Captured | **Checked** | Seed and session created; ordering of its 10121–10124/50171–50174 sequence covered |
| Launch_OnCloseLoadingLog | 8 | none | Captured | **ACK only** | Telemetry |
| BattlePassLogic_ClientRefresh | 7 | `[]` (empty list) | BattlePass/BattlePassLogic_ClientRefresh | **Matched** | Now replies `[]`; the current battle pass still goes out through the attribute sync. Checked only with no battle pass configured |
| Chapter_NewPrologueSettlement | 7 | `{bWaitServer,nID}` direct; wrapped returns `{bWaitServer,nID,tbShowAward}` | Captured + Chapter_DealLevelSettlement | **Checked** | Zero-seed checkpoint with no rewards; wrapped 50174 grants plot rewards. Full checkpoint semantics need local-client validation |
| Chapter_DealLevelSettlement | 5 | `{sCmd,tbParam}` with `tbParam=[[[g,d,p,l,count,cat(,stackId)]]]` | Chapter/Chapter_DealLevelSettlement | **Matched** (Chapter) | Plot and combat seed binding, first+base drops, row shape checked. Daily/Role shape unobserved; random-drop category 3 assumed; error shape (`sErr` in `tbParam`) unobserved |
| Activity_Flag | 5 | `ActivityQuest_Flag {nId,nType}` | Captured | **Checked** | Echo shape only; what the flag drives on the client is unverified |
| Chapter_SyncGuideLevelPassData | 4 | none (a `Player_OnDaily null` appeared nearby, likely unrelated) | Chapter/Chapter_SyncGuideLevelPassData | **Checked** | Rejects passCount>1 and levels neither active nor passed |
| Activity_GetAward | 4 | `DaySign {nId,nType,tbRewards}`; `ActivityQuest_GetReward {nId,nQuestId,tbRewards}` | Captured | **Partial** | Sign + quest types 187/350 checked with duplicate and template prevalidation; activity gating implemented (see Activity section); house quest types rejected |
| Chapter_EnterLevel | 4 | `{nSeed}` | Chapter/Chapter_EnterLevel | **Matched** | Used for both plot (51471/51472) and combat (11421/11422) |
| Lineup_Update | 4 | `UpdateLineup null` | Lineup/Lineup_Update | **Matched** | Ownership, distinct members, index 1..9, name before save/notify |
| Online_ClientLog | 4 | none | Captured | **ACK only** | Telemetry |
| FavMailLog | 3 | none | Captured | **ACK only** | Telemetry |
| LanguageChange | 2 | none | Misc/LanguageChange | **ACK only** | No persistence |
| OnlineAssaultOpsBattlePass_ChangeBP | 1 | `{nId}` echo, `Custom[12058625]=1` | Captured | **Matched** | |
| ReqEntranceGreenLevel | 1 | list of `[skinId, level]` pairs | Preview/ReqEntranceGreenLevel | **Stub** | Emulator always returns `[]`; retail returned 14 pairs at level 50 |
| Daily_GetActivityInfo | 1 | `daily/daily.json` `Activity` per row, array indexed by ID (99 entries) | Daily/Daily_GetActivityInfo | **Matched** | Built from the configured resource root; exact reply checked. Real `daily.json` (IDs 1–8, 99) reproduces the retail array |
| SettingChange | 1 | none | Misc/SettingChange | **ACK only** | Persists settings locally; not compared |
| Activity_Refresh | 1 | `ActivityQuest_Refresh {nId}` | Activity/Activity_Refresh | **Checked** (shape) | |
| GuideLogic_SetBeginner | 1 | `null`, `Custom[12255233]=1` | Captured | **Matched** | Only nType 1 observed; type 2 handling inferred |
| ActivityFace_Update | 1 | `{nId,nType}` echo | Activity/ActivityFace_Update | **Checked** (shape) | Persists local state |
| LimitedTime_CheckLimitedRoleGet | 1 | none observed | Captured | **Stub** | ACK only. No entitlement or reward resolver; request carries `{nGuideId,nStepId,tbGDPL}`. Unresolved |
| Gacha_Launch | 1 | `{tbAwards,tbTrigger,nId,tbNew,nBoxCount}` | Gacha/Gacha_Launch | **Partial** | Same keys as retail; drop rates, pity and banner rules not compared |
| GirlCard_UpdateLevel | 1 | `null` + item sync | Girl/GirlCard_UpdateLevel | **Matched** | Materials are stack ids from settlement; atomic rejection; level cap from configured `level_limit.json` |
| Activity_QuickQuestAward | 1 | `{nId,tbIdList,tbRewards}` | Captured | **Partial** | Batching checked. Retail batch for activity 23 (1402,1403,1407,1408,1410,1415) is entirely house quest types, so the emulator rejects it |
| IBLogic_GoodsRedDot | 1 | `{tbList}` echo | Shop/IBLogic_GoodsRedDot | **Matched** | Echoes the request; red-dot attributes still set |

Summary (29 APIs): 10 Matched, 6 Checked, 4 Partial, 0 Mismatch, 2 Stub, 7 ACK only.

## Activity section

- Activity open gating evaluates StartTime/EndTime (yyyyMMddHHmm read as UTC, unverified) and `Condition` as alternatives of AND-ed clauses (also inferred).
- Only clause type 1 (player level, inferred) and type 2 (level passed) are resolved. Type 15 and the dictionary-shaped conditions (activities 17, 283, 383) fail closed.
- Sign reset is a UTC calendar-day policy inferred from the capture; the retail reset hour and timezone are unknown.
- Claims are prevalidated before any grant (template and genre) but are not crash-atomic.

## House section

Observed FuncNames and whether a House_Func handler names them:

| FuncName | Count | Handled |
|---|---:|---|
| GiveGiftToArea | 4 | yes (HouseShared) |
| GirlRegister | 2 | yes (HouseBedroom/HouseShared) |
| SavePuzzleMapState | 2 | **no** |
| GameEnterMainUI | 1 | yes (HouseThrow) |
| ThrowGameEnter | 1 | yes (HouseThrow) |
| ThrowGameSettlement | 1 | yes (HouseThrow) |
| ThrowGameTutorialFinish | 1 | yes (HouseThrow) |
| DoDailyTalk | 1 | **no** |
| ExchangeGift | 1 | **no** |
| GetPuzzleMapReward | 1 | **no** |
| LogHouseStoryFinish | 1 | **no** |
| QuickCollectDrop | 1 | **no** |

"Handled" means only that a handler exists; none of the House responses have been compared against the capture.

House achievement counters (quest types 117 fragments collected, 118 analyses completed, 120 random events, 122 furniture gifted, 124 Comfort level) have no authoritative emulator source. Activity quest claims that depend on them are rejected.

## Unresolved items, in priority order

1. Five unhandled House FuncNames and the house counters they likely feed.
2. LimitedTime_CheckLimitedRoleGet entitlement: no evidence of what the server grants; needs a capture where the server pushes a role or reward after it.
3. ReqEntranceGreenLevel data source (per-skin level table).
4. Local-client acceptance for every row; none has been run against this build.

## Reproducing

```powershell
dotnet run --project tests/MikuSB.CaptureChecks -v quiet
```

The per-API field summary was generated with a throwaway script that reads only decoded `api`/`param`/`arg` keys from the JSONL; it doesn't output credentials or raw payloads.

## Second capture — shop session 2026-10-25

Source stem `snowbreak-official-20261025-175235-234222`: 43 ReqCallGS. The capture does **not** contain IBLogic_ShoppingCart, OnlineActivity_Request or opcode 321 (ReqChangeWorldChannel), so those three are still unresolved.

| API | Observed | Retail reply | Status | Notes |
|---|---:|---|---|---|
| ShopLogic_GetGoodsList | 10 | `{shopId, goodsList:[{goodsid, discountindex:0}]}` | **Matched** (9 shops) / **Checked** (shop 12) | Shops with every row slotted list all rows, and the real resources reproduce the retail sets for shops 13, 14, 19, 20, 25, 50–53. Shop 12 (unslotted weighted pool) rolls `Columns` rows: one per PosIndex group plus a weighted draw. The roll persists until RefreshTime 04:00 UTC+8. RefreshRule semantics, list order, discounts and manual refresh are unverified. Fixture `tests/MikuSB.CaptureChecks/Fixtures/retail-shop-20261025.json` holds the retail replies (config-derived, no account data) |
| ShopLogic_GetOpenTime | 1 | `{nBegin,nEnd}` array indexed by ShopId (404 entries) | **Matched** | shop_tab Begin/End + dlc_shop StartTime/EndTime as UTC+8. 67 entries identical; 9 differ only because the local resource set is older (145–148, 305–308, 404) |
| PlayerSetting_SetBirthday | 1 | `null`, custom 151/1 = current year | **Checked** | Retail also put month/day into NtfSyncPlayer.show_attrs at indices that aren't verified; the emulator keeps month/day locally only |
| DownloadMultMediaLog | 4 | none | **ACK only** | Mail media download telemetry |
| Gacha_UpSelect | 1 | `[]` | not compared | Existing handler |
| Achievement_OnAddSomeOne | 1 | none observed | not compared | Existing handler |

Config times are UTC+8. This also changed activity StartTime/EndTime parsing, which earlier assumed UTC.
