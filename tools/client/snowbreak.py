"""Local preservation inventory and JSON editing workspace. Python 3.11+, stdlib only."""
import argparse
import hashlib
import json
import struct
from pathlib import Path

DEFAULT_CLIENT = Path(r'E:/SteamLibrary/steamapps/common/SNOWBREAK')
DEFAULT_RESOURCES = Path(r'E:/Projects/MikuSB-Resource')

def sha256(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()

def inventory(client, resources):
    exe = client / 'Game/Binaries/Win64/Game.exe'
    paks = []
    for path in sorted((client / 'Game/Content/Paks').rglob('*.pak')):
        size = path.stat().st_size
        with path.open('rb') as f:
            f.seek(max(0, size - 4096))
            tail = f.read()
        # Candidate metadata only: footer layout and index encryption remain unresolved.
        at = tail.rfind(b'\xe1\x12\x6f\x5a')
        candidate = None
        if at >= 0 and at + 24 <= len(tail):
            version, offset, length = struct.unpack_from('<iqq', tail, at + 4)
            candidate = dict(version=version, index_offset=offset, index_length=length,
                             bounds_valid=0 <= offset <= size and 0 <= length <= size-offset)
        paks.append(dict(path=str(path.relative_to(client)), bytes=size, footer_candidate=candidate))
    loc = client / 'Localization.txt'
    return dict(status='HOST FACT; no client compatibility or visual acceptance implied',
                executable=dict(path=str(exe), bytes=exe.stat().st_size, sha256=sha256(exe)),
                localization=loc.read_text(encoding='utf-8-sig') if loc.exists() else None,
                resources=dict(path=str(resources), json_files=sum(1 for _ in resources.rglob('*.json'))),
                paks=paks)

def export_json(resources, relative, output):
    root = resources.resolve()
    source = (root / relative).resolve()
    if not source.is_relative_to(root):
        raise ValueError('Resource path must stay inside the resource root')
    target = output.resolve()
    if target.is_relative_to(root) or target.exists():
        raise ValueError('Choose a new output outside the resource tree')
    value = json.loads(source.read_text(encoding='utf-8-sig'))
    target.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive create prevents overwriting an existing editing workspace.
    with target.open('x', encoding='utf-8') as f:
        json.dump(value, f, ensure_ascii=False, indent=2)
        f.write('\n')
    return dict(source=str(source), source_sha256=sha256(source), edited_copy=str(target))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, default=DEFAULT_CLIENT)
    parser.add_argument('--resources', type=Path, default=DEFAULT_RESOURCES)
    sub = parser.add_subparsers(dest='command', required=True)
    inv = sub.add_parser('inventory'); inv.add_argument('--out', type=Path, required=True)
    export = sub.add_parser('export-json'); export.add_argument('relative'); export.add_argument('output', type=Path)
    validate = sub.add_parser('validate-json'); validate.add_argument('path', type=Path)
    args = parser.parse_args()
    if args.command == 'inventory':
        result = inventory(args.client, args.resources)
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print(f"Inventoried {len(result['paks'])} PAK files; {result['resources']['json_files']} resource JSON files")
    elif args.command == 'export-json':
        print(json.dumps(export_json(args.resources, args.relative, args.output), indent=2))
    else:
        value = json.loads(args.path.read_text(encoding='utf-8-sig'))
        print(f'Valid JSON ({type(value).__name__}); schema and gameplay semantics unverified')

if __name__ == '__main__':
    main()
