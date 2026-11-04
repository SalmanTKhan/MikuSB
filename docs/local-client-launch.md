# Verified local client launch — 2026-10-25

## Confirmed outcome

Steam client 3.6.0.186 reached the rendered character lobby against the local MikuSB host. The user supplied the lobby screenshot at `E:/Temp/codex-clipboard-407932f6-0062-4cf5-8ec1-bed990b91321.png`.

At verification, server PID 51864 listened on loopback TCP 21000, SDK TLS 21500/13443/31443, and SOCKS5 18888. Client PID 27396 had an established connection from `127.0.0.1:3965` to `127.0.0.1:21000`; the server owned the reverse connection. These PIDs and ephemeral ports are session-specific.

Server logs confirmed POST `/getGameConfig`, `/seasun/config`, `/seasun/login`, server-list requests, and subsequent ReqCallGS traffic including BattlePassLogic_ClientRefresh, Daily_GetActivityInfo and ZoneTime_ReqTime. Client logging explicitly reported Get game config success. Live game packets used the inherited TwoByteBigEndianLength wrapper; the packet-viewer plugin should use `--opt framing=wrapper` for this session.

## Launch

`start-server.bat` starts the host. After it is ready, `start-client.bat` starts only the client, through the standalone ClientLauncher. Both use offline mode to prevent automatic binary replacement/downloads.

The standalone launcher is built with:

```powershell
dotnet build tools/ClientLauncher/ClientLauncher.csproj
```

It shares the main application's output folder so existing configuration-relative patch paths resolve correctly. Stop the host before rebuilding into that folder to avoid Windows DLL locks. The server also accepts `--launch-client` to launch after initialization, and `--headless` for redirected service output. Current server stdout is `docs/launch-server.local.log`, stderr `docs/launch-server-error.local.log`.

The client uses an isolated user directory beneath the main build output, with logs in `Client_User_Data/Saved/Logs/Game.log`. Runtime hook logs are in `Patch/Patch.log`. No executable on disk, Windows hosts file, global proxy or certificate trust store was modified.

## Runtime redirect DLL provenance

Built from `https://github.com/Kei-Luna/MikuSB-Patch`, commit `5584f9357500d9ff277050269e0210d086ded329`. MinHook was pinned to `8af6b4acae5a9388fd742b56fa79ece89d96f823`. The local source delta is `tools/client/runtime-local.patch`; source checkout/build currently reside in `E:/Temp/MikuSB-Patch-source`, `E:/Temp/MikuSB-minhook-source`, and `E:/Temp/MikuSB-Patch-build`.

Installed output: `MikuSB/bin/Debug/net10.0/Patch/MikuSB-Patch.dll`.

SHA-256: `97ac4b47ed9fbd3ab99e70355caed2ee1c3f556a6bd9f1493b9dd89a30526976` (MSVC 19.44 Release x64 build).

Reproduce in fresh source directories:

```powershell
git clone https://github.com/Kei-Luna/MikuSB-Patch .local/runtime-patch
git -C .local/runtime-patch checkout --detach 5584f9357500d9ff277050269e0210d086ded329
git -C .local/runtime-patch apply ../../tools/client/runtime-local.patch
git clone https://github.com/TsudaKageyu/minhook .local/minhook
git -C .local/minhook checkout --detach 8af6b4acae5a9388fd742b56fa79ece89d96f823
cmake -S .local/runtime-patch -B .local/runtime-build -G "Visual Studio 17 2022" -A x64 -DFETCHCONTENT_SOURCE_DIR_MINHOOK="$PWD/.local/minhook"
cmake --build .local/runtime-build --config Release
# With the client stopped:
Copy-Item .local/runtime-build/Release/MikuSB-Patch.dll MikuSB/bin/Debug/net10.0/Patch/MikuSB-Patch.dll
```

The active upstream Install path hooks getaddrinfo/connect/WSAConnect imports in the process, including the dynamically loaded xggamesdk and xg_seasun_channel modules. It redirects SDK DNS for xgsdk.xoyo.games and game port 5200 to localhost:21000. The unused SDK/login/SSL patch functions in upstream source were not enabled.

## Cause of the stalled Americas attempt

The SDK used direct DNS/socket calls independently of the game HTTP proxy. DNS was successfully redirected to 127.0.0.1, but its connection retained port 18443, which Windows reserves on this host. The request failed before reaching `/getGameConfig`.

The local runtime delta maps loopback 18443 to 21500, logs the actual destination port, and makes the log share-readable. The server proxy also maps 18443 to the configured main SDK TLS port when 18443 is not an enabled alias. After restart the runtime log confirmed `127.0.0.1:18443 -> 127.0.0.1:21500`, and the client accepted the SDK response and proceeded to login/lobby.

## Remaining boundaries

Lobby rendering and a live local game connection are confirmed. Combat, progression, multiplayer and save/relogin behavior have not been accepted. Current logs include missing announcement/jump configuration URLs (404), a Lua S2C dispatch error, and missing preview asset/material warnings. These remain compatibility work; generic successful responses do not establish correct feature behavior.

A synthetic SDK test also created local account `snowbreak@local.invalid` (UID 2). No real credentials were entered by the agent. The successful visible lobby login was performed by the user.
