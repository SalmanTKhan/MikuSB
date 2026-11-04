# Steam executable triage — 2026-10-25

## Scope and identity

Read-only Python file inspection plus Ghidra MCP using the existing instance at HTTP `127.0.0.1:8089`, explicitly selecting `/Snowbreak/Game.exe`. No client process was launched, executable changed, or Ghidra analysis settings modified.

File: `E:/SteamLibrary/steamapps/common/SNOWBREAK/Game/Binaries/Win64/Game.exe`, 131,977,096 bytes.

SHA-256: `291aeb415600bb38e8354f35aa48f2a5b75193029b458fdf5627484b6aa8e81a`.

## Confirmed evidence

- Image base `0x140000000`; PE entry VA `0x1451c7f3c`.
- Nine conventional sections. `.text` raw size 87,603,200 bytes, virtual size 87,602,719 bytes, entropy 6.616 bits/byte, flags `0x60000020` (executable/readable, not writable). `.rdata` entropy 5.169. High `.rsrc` entropy (7.840) is not evidence that executable code is encrypted.
- 1,047 normal imports across 44 DLLs, including WS2_32, WINHTTP, CRT and graphics libraries. A separate complete PE parse reported 651 exports.
- Ghidra reported `analyzing=true`, `analyzed=true`, function counts 250,982 and then 265,934 during this investigation. These flags are not completion percentages. The increasing count establishes progress despite the reported 0% UI display.
- Targeted decompilation worked while auto-analysis was running.

| VA | Observed behavior | Interpretation |
|---|---|---|
| `0x1451c7f3c` | Calls `0x1451cc630`, then `0x1451c7dc8` | PE startup entry |
| `0x1451cc630` | Checks `0x2b992ddfa232`, combines time/thread/process/performance-counter values and stores cookie/complement | Inferred MSVC security-cookie initialization |
| `0x1451c7dc8` | Calls `_initterm_e`, `_initterm`, obtains WinMain command line, invokes `0x14089bcd0` | Inferred CRT startup path |
| `0x14089bcd0` | Reads `GetCommandLineW`, checks argument strings, creates a mutex, branches into application initialization | Inferred WinMain-shaped function; prototype remains unverified |

Ghidra's raw entry bytes match ordinary x64 startup instructions: `4883ec28 e8eb460000 4883c428 e97afeffff`. No unpacking stub was demonstrated along this inspected path.

## Raw-string anchors for connection research

These are file literals, not proof that a route is selected at runtime:

| VA | Literal |
|---|---|
| `0x1458b13d8` | `https://xgsdk.xoyo.games:13443` |
| `0x1458b1408` | `https://xgsdk.xoyo.games:18443` |
| `0x1458b14b0` | `https://account.amazingseasun.com` |
| `0x1458b6900` | `dispatchUrl` (ASCII) |
| `0x14546d558` | `dispatchUrl` (UTF-16LE) |
| `0x146bb8db8` | `ALL_PROXY` |
| `0x14544c879` | `ReqCallGS.api` |
| `0x145aa0370` | `https://snowbreak-content.amazingseasuncdn.com/ob202307/webfile` (UTF-16LE) |

Python also found ReqLogin/ReqCallGS occurrences in binary descriptor-like data. The bounded search retains up to eight occurrences per term per encoding; it is not an exhaustive string inventory. Raw matches outside mapped sections have file offsets but no VA. A libcurl-related ALL_PROXY literal does not establish whether the SDK or game networking honors that environment variable.

## Assessment and next boundary

**INFERRED:** whole-image packing is unlikely given ordinary startup, readable code/data, normal imports and successful decompilation. This does not exclude protection on selected functions or dynamically loaded modules.

**UNRESOLVED:** the reason the UI shows 0%; which analyzer is consuming time; endpoint selection and configuration precedence; proxy usage; certificate validation; and live connection behavior.

Use Python raw-byte/PE searches and targeted Ghidra decompilation instead of waiting for full analysis. Follow references to `dispatchUrl` and SDK endpoint literals, then verify runtime endpoint selection before implementing redirection. A missing defined string or xref in incomplete Ghidra analysis is not evidence that it is absent from the binary.

## Reproduce

```powershell
python -m pip install -r tools/client/requirements.txt
python tools/client/inspect_pe.py E:\SteamLibrary\steamapps\common\SNOWBREAK\Game\Binaries\Win64\Game.exe --out docs/pe-inspection.local.json
```

The script uses `fast_load=True` and parses only the import directory, then performs bounded raw-string searches. This avoids the additional work of traversing every PE directory. The JSON report is ignored as a host artifact. A full parse completed too, but took substantially longer; neither result depends on Ghidra finishing auto-analysis.
