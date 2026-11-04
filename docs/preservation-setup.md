# Snowbreak preservation setup â€” 2026-10-25

The original Steam client has since reached the local lobby. See [verified launch](local-client-launch.md) for the runtime DLL, port fix and live acceptance evidence. Earlier unverified boundaries below describe the initial foundation check.

## Run locally

From `E:\Projects\MikuSB`, run `start-server.bat`. This builds and starts the existing .NET 10 emulator in offline mode. Ctrl+C saves and shuts down. The installed SDK is pinned by global.json with feature-band roll-forward. Configuration uses the existing sibling `E:/Projects/MikuSB-Resource` and Steam `Game/Binaries/Win64/Game.exe`.

Endpoints bind to loopback: game TCP 21000, SDK HTTPS 21500 plus 13443/31443, SOCKS5 18888. Windows reserves TCP 18428â€“18527 on this host, so the inherited TLS alias 18443 is excluded through `HttpServer.AdditionalTlsPorts`. Client behavior that requires that alias remains unresolved. No Windows reservation, hosts file, system proxy, or trust store was changed.

Offline mode (`MIKUSB_OFFLINE=1`) disables upstream executable/resource/runtime-patch downloads. The optional in-game console is disabled in the local configuration. The existing `game` command requires a reviewed `MikuSB-Patch.dll` in the output `Patch` directory; that DLL was absent during the initial foundation check. The subsequent source-built runtime hook and successful lobby entry are documented in local-client-launch.md.

## Transport and persistence

The existing gameplay handlers and protobuf sources are retained. Yggdrasil accepts connections and queues socket sends; the compatibility class supplies a bounded pipe to the existing async codec. Registration precedes Start; concurrent session lookup replaces the old unsynchronized sorted list. Listeners start after handlers, database and resources, and shutdown closes them before saving.

The TCP source subset is pinned to the upstream commit recorded by package 2.0.11. See `TcpSharp/Vendor/Yggdrasil/README.md` and its MIT license. Local changes fix shutdown retries, remote-EOF cleanup and partial writes. These are host transport fixes, not retail protocol findings.

SQLite remains the default (`Config/Database/Miku.db`). To select MySQL, create an empty database, set `Database.Provider` to `MySql`, and supply its connection string through `MIKUSB_MYSQL_CONNECTION` (or the configured environment variable name). SqlSugar retains existing table/JSON mappings; quest-flag SQL now uses quoting valid on both providers. MySQL integration is configured but **not live-tested**. Existing SQLite data is not migrated automatically. Back up data before changing providers. Initialization and table-creation failures propagate instead of silently continuing or waiting forever.

## Client inventory and editing

```powershell
python tools/client/snowbreak.py inventory --out docs/client-inventory.local.json
python tools/client/snowbreak.py export-json config.json tools/client/edited/config.json
python tools/client/snowbreak.py validate-json tools/client/edited/config.json
```

Inventory hashes the executable and lists PAK sizes and candidate footer/index bounds without extracting assets. JSON export creates a new editable copy outside the resource tree and refuses overwrite/path traversal. Validation checks JSON syntax only. Edited files are not automatically deployed or repacked. The [PAK tool](pak-tool.md) now reads Snowbreak version 12 encrypted indexes and extracts verified encrypted uncompressed/zlib payloads, as well as legacy mods. All 633 installed retail indexes passed hash verification. Asset interpretation, editing and repacking remain separate work.

The inspected installation has 634 PAK files and 4,580 resource JSON files. The inventory is ignored as host-specific output. Resource build_info.json identifies `DLC36_publish_oversea` with a March 23, 2026 timestamp; matching these resources to the Steam binary is unresolved.

`Localization.txt` already contained `localization = 1`, and `Game/Content/Paks/mods/zzz_Fixgamecensor_100_P.pak` was present (257,651,711 bytes). Both were left as found. Their visual effect and provenance are unverified.

## Packet viewer

The plugin is installed in `E:/Projects/PyPacketViewWeb/plugins/snowbreak`. Run from that repository:

```powershell
python -m pktscope pcap --plugin snowbreak capture.pcapng --web
python -m pktscope relay --plugin snowbreak --route 21001=127.0.0.1:21000 --web
```

The relay requires a test client redirected to 21001. Default framing is the source-derived u32 LE length/u16 LE opcode layout. `--opt framing=wrapper` selects the inherited 4+35-byte wrapper. Explicit selection avoids ambiguous autodetection. Opcode names are a snapshot of `Proto/CmdIds.cs`; schemaless protobuf is not a recovered schema. Login/reconnect/CallGS payloads are redacted by default. Compression, encryption and capture-gap resynchronization remain unresolved; do not treat raw chunks as packets. No original-client capture was made.

## Validation

```powershell
dotnet build MikuSB.sln
dotnet run --project tests/MikuSB.FoundationChecks
$env:MIKUSB_OFFLINE='1'
dotnet MikuSB/bin/Debug/net10.0/MikuSB.dll --smoke-test
python tests/host_smoke.py
# In PyPacketViewWeb:
python -m unittest discover -s tests -p test_snowbreak.py
```

Confirmed: clean solution build; isolated SQLite account/quest-flag round trips; Yggdrasil fragmented frame handling, 4 MiB send, disconnect cleanup and listener shutdown/restart; complete host startup/resource load, HTTPS 200, listener checks and ten-second smoke shutdown with exit code 0; three Python packet-plugin tests. Smoke listeners are stopped afterwards. Logs are local ignored artifacts.

Ghidra MCP at HTTP 8089 confirms `/Snowbreak/Game.exe`, PE x64, image base 0x140000000, 250,117 functions. A bounded defined-string search for ReqLogin/ReqCallGS/seasungames/snowbreak/dispatch only returned Windows DispatchMessage imports; it does not establish the game protocol. No Ghidra program was modified.

Next acceptance boundary: identify and verify runtime redirection against the SHA-256 recorded in the inventory, capture the installed client, then reconcile SDK routes, framing, opcode/schema version and resource version. The inherited feature checklist is not fresh original-client acceptance. No login, lobby, combat, multiplayer or visual parity is claimed by this setup.
