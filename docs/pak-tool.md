# Snowbreak PAK tooling

The reader supports the installed Steam version 12 archives, including encrypted primary/secondary indexes, encoded file entries, encrypted payloads and zlib blocks. It also supports standard unencrypted version 3–7 archives such as the installed mod. Archives are opened read-only; extraction never installs or repacks files.

## Commands

Run from the repository root with Python 3.11+. Install the optional crypto dependency:

```powershell
python -m pip install -r tools/client/requirements-pak.txt
```

The verified master key is in the ignored host-local file `.local/snowbreak-pak-key.txt`. It is supplied explicitly and is never printed or embedded in reports. That local file must be provided separately on another machine. Keep archive filenames unchanged: the decryption key depends on the archive basename.

```powershell
# Footer audit only; no key needed:
python tools/client/pak.py scan "E:/SteamLibrary/steamapps/common/SNOWBREAK/Game/Content/Paks" --out .local/pak-audit.json

# Decrypt and validate every retail index without extracting all assets:
python tools/client/pak.py scan "E:/SteamLibrary/steamapps/common/SNOWBREAK/Game/Content/Paks" --key-file .local/snowbreak-pak-key.txt --out .local/pak-verified.json

# List one archive:
python tools/client/pak.py list "E:/SteamLibrary/steamapps/common/SNOWBREAK/Game/Content/Paks/APP_Game_Startup_Settings_0-WindowsNoEditor.pak" --key-file .local/snowbreak-pak-key.txt --out .local/settings-index.json

# Extract matching entries to a NEW folder:
python tools/client/pak.py extract "E:/SteamLibrary/steamapps/common/SNOWBREAK/Game/Content/Paks/APP_Game_Startup_Settings_0-WindowsNoEditor.pak" .local/settings-export --key-file .local/snowbreak-pak-key.txt --glob "*.txt"
```

`--profile auto` means Snowbreak handling for version 12 and standard handling for legacy archives. Use `--profile standard` when inspecting another game's PAK; do not apply Snowbreak's XORs to unrelated archives. Version 8's alternate entry layouts and other modern profiles are unsupported.

Reports and extraction folders must be new. `scan` records errors per archive, so inspect its status fields before treating a scan as successful. Archive listings use names relative to the mount point; the mount is stored separately. Extracted files retain their original archive bytes after PAK decryption/decompression. Lua may have a separate encryption layer and is not automatically transformed. Unreal asset interpretation, editing, repacking and mod deployment are separate work.

## Resolved format details

- Footer index offset: stored value XOR `0x1C1D1E1F` for the Snowbreak profile.
- Encoded file offset: stored value XOR `0x1F1E1D1C`.
- Per-archive key: lowercase archive basename without extension, ASCII MD5 digest represented as 32 lowercase hex characters, encrypted using AES-256-ECB with the master key. The resulting 32 bytes are the AES-256-ECB key for that archive's indexes and payloads.
- Primary index includes mount, file count, path-hash seed, secondary index locations/hashes, encoded entry blob and nonencoded entries. Full directory references decode the entry blob. Directory root `/` is joined relative to the mount.
- All three index regions are decrypted and verified against their recorded SHA-1 hashes. Hashed regions include serialized index padding; padding is not assumed zero. Path-hash references and entry counts are checked against primary/directory data.
- Payload headers are compared with decoded index entries before extraction. Encrypted compressed blocks occupy 16-byte-aligned storage. Their stored-data SHA-1 includes ciphertext padding; uncompressed payload hashes cover exactly the declared ciphertext length, excluding alignment padding. Both conventions were verified against installed files.

The offset and crypto rules were adapted from [CUE4Parse](https://github.com/FabianFG/CUE4Parse) at commit `77b94fa26cde08d34e80be2ced29c8f8885ff0a7`:

- [FPakInfo.cs](https://github.com/FabianFG/CUE4Parse/blob/77b94fa26cde08d34e80be2ced29c8f8885ff0a7/CUE4Parse/UE4/Pak/Objects/FPakInfo.cs)
- [FPakEntry.cs](https://github.com/FabianFG/CUE4Parse/blob/77b94fa26cde08d34e80be2ced29c8f8885ff0a7/CUE4Parse/UE4/Pak/Objects/FPakEntry.cs)
- [SnowbreakAes.cs](https://github.com/FabianFG/CUE4Parse/blob/77b94fa26cde08d34e80be2ced29c8f8885ff0a7/CUE4Parse/GameTypes/Snowbreak/Encryption/Aes/SnowbreakAes.cs)

Index/entry layout guidance also comes from `PakFileReader.cs` at that commit. CUE4Parse is Apache-2.0; its license is retained under `tools/client/third_party/CUE4Parse.LICENSE`. This is an adaptation of published format research followed by validation on local archives, not an independent clean-room discovery. The candidate master key came from [QiYun's published extraction notes](https://qiyun71.github.io/Project/Unpack%20Snowbreak%20File/); its applicability to this installation was established by the index hashes, not assumed from the post's age. No live-client attachment or modification was needed.

## Installed-client validation, 2026-10-25

Footer scan: 634 archives, consisting of 633 encrypted version 12 retail archives and one version 3 mod. The old generic reader reported 591 out-of-bounds offsets; applying Snowbreak's verified XOR puts all 633 retail index ranges in bounds.

Full index audit: **633/633 retail archives verified**, with **710,843 indexed files**, all marked encrypted. Primary, path-hash and full-directory index hashes passed. No retail archive failed. Saved evidence: `.local/pak-retail-verified-audit.json`.

Extraction: all four entries from startup settings passed stored-data hashes and zlib decompression. Eleven additional samples across engine, preload configuration, non-UFS patch and character archives passed, totaling 803,762 expanded bytes, including encrypted uncompressed and multi-block zlib payloads. Reports and sampled files remain in `.local/pak-retail-samples/`. These are representative extraction checks, not a full 710,843-file payload verification or in-game asset acceptance.

The existing mod's version 3 index SHA-1 passed and 846 entries were listed (811 zlib, 35 uncompressed). One compressed mod asset was extracted earlier. Its provenance and in-game visual effect remain unverified.

Thirteen automated tests cover legacy extraction, unsafe paths, corrupt/truncated structures, inflation limits, encrypted multi-block/uncompressed extraction, wrong/missing keys, renamed archives, directory/payload corruption and explicit standard-profile behavior:

```powershell
python -m unittest discover -s tests -p "test_pak*.py" -v
```

Limits: 128 MiB per index, 256 MiB per stored/expanded file. Encryption, block bounds, hashes and output sizes are checked before files are written. Unsupported compression methods and deleted entries fail explicitly. Failed extraction may leave diagnostic partial output; rerun into a new directory. All inventories, keys and extracted assets remain in ignored `.local/` or `*.local.json` outputs.
