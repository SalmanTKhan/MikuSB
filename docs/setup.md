# Local setup

Install the .NET 10 SDK and Python 3, and obtain compatible resource data separately. The repository does not provide game assets. From the repository root run `setup.bat` on Windows or `python3 tools/setup.py` on Linux/macOS. Supply the resource directory and, on Windows, optionally the path to `Game.exe`. For scripted setup use `python tools/setup.py --resources <directory> --game <Game.exe>`; omit `--game` for server-only use.

Setup creates `Config/Config.local.json`, which takes precedence over `Config/Config.json` and is ignored by Git. Edit that file to change ports or other settings. Existing local configuration is preserved unless `--force` is supplied. The resource directory must contain compatible data; checking that the directory exists does not establish version compatibility.

Start the server with `start-server.bat` on Windows or `sh start-server.sh` on Linux/macOS. These scripts disable automatic downloads. The first run restores and builds dependencies. Keep the server running while using the client. SQLite data is stored in `Config/Database` by default. Stop the server with Ctrl+C.

For the Windows client, build and install the runtime redirect DLL as described in [local client launch](local-client-launch.md), then run `start-client.bat`. It builds the launcher on first use. The documented Windows Steam client reached the lobby; this does not establish complete gameplay compatibility. The launcher uses Windows APIs and does not run natively on Linux or macOS. [Linux/Proton instructions](../README_linux.md) cover server setup and diagnostics; client login remains unverified. No macOS client setup is currently verified.

Local resources, client binaries, runtime builds, captures, databases, certificates and machine-specific configuration should stay outside version control. Ignore rules do not remove files already tracked by Git.
