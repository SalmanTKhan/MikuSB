"""Read-only Snowbreak v12 and legacy PAK reader. Format provenance: docs/pak-tool.md."""
import argparse
import fnmatch
import hashlib
import json
from pathlib import Path, PurePosixPath
import struct
import zlib

MAGIC = bytes.fromhex('e1126f5a')
MAX_INDEX = 128 * 1024 * 1024
MAX_FILE = 256 * 1024 * 1024


def footer(path, profile="auto"):
    path = Path(path)
    size = path.stat().st_size
    with path.open('rb') as file:
        file.seek(max(0, size - 4096)); tail = file.read()
    candidates = []
    at = 0
    while True:
        at = tail.find(MAGIC, at)
        if at < 0: break
        if at + 44 <= len(tail):
            version, offset, length = struct.unpack_from('<iqq', tail, at + 4)
            if 1 <= version <= 12:
                position = max(0, size - 4096) + at
                start = position - (17 if version >= 7 else 1 if version >= 4 else 0)
                encrypted = bool(tail[at-1]) if version >= 4 and at else False
                raw_offset = offset
                # Snowbreak's retail profile is version 12; legacy mods stay standard.
                snowbreak = version == 12 and profile in ('auto', 'snowbreak')
                if snowbreak: offset ^= 0x1C1D1E1F
                methods = []
                if version >= 8:
                    names = tail[at+44:at+204]
                    if len(names) != 160: raise ValueError('Truncated compression method table')
                    methods = [names[i:i+32].split(b'\0',1)[0].decode('ascii') for i in range(0,160,32)]
                valid = start >= 0 and 0 <= offset <= start and 0 <= length <= start - offset
                candidates.append(dict(version=version, magic_offset=position, footer_offset=start,
                    index_offset=offset, stored_index_offset=raw_offset, offset_transform="xor-1c1d1e1f" if snowbreak else "none",
                    profile="snowbreak" if snowbreak else "standard", compression_methods=methods,
                    index_bytes=length, index_bounds_valid=valid,
                    index_encrypted=encrypted, index_sha1=tail[at+24:at+44].hex(),
                    index_key_guid=tail[at-17:at-1].hex() if version >= 7 and at >= 17 else None))
        at += 4
    if len(candidates) != 1:
        raise ValueError(f'Expected one supported footer candidate; found {len(candidates)}')
    result = dict(path=str(path), bytes=size, **candidates[0])
    result['status'] = ('index-offset-unresolved' if not result['index_bounds_valid'] else
                        'encrypted-index' if result['index_encrypted'] else
                        'legacy-readable' if 3 <= result['version'] <= 7 else 'index-format-unsupported')
    return result


class Reader:
    def __init__(self, data): self.data, self.at = data, 0
    def take(self, size):
        if size < 0 or self.at + size > len(self.data): raise ValueError('Truncated PAK structure')
        value = self.data[self.at:self.at+size]; self.at += size; return value
    def unpack(self, fmt): return struct.unpack(fmt, self.take(struct.calcsize(fmt)))
    def string(self):
        length, = self.unpack('<i')
        if abs(length) > 1024 * 1024: raise ValueError('PAK string too large')
        if length == 0: return ''
        raw = self.take(abs(length) * (2 if length < 0 else 1))
        terminator = b'\0\0' if length < 0 else b'\0'
        if not raw.endswith(terminator): raise ValueError('PAK string lacks terminator')
        return raw[:-len(terminator)].decode('utf-16-le' if length < 0 else 'utf-8')


def entry(reader, version):
    start = reader.at
    offset, stored, unpacked, compression = reader.unpack('<qqqI')
    digest = reader.take(20).hex()
    blocks = []
    if compression:
        count, = reader.unpack('<i')
        if not 0 <= count <= 100000: raise ValueError('Invalid compression block count')
        blocks = [reader.unpack('<qq') for _ in range(count)]
    flags, block_size = reader.unpack('<BI')
    if min(offset, stored, unpacked) < 0: raise ValueError('Negative entry bounds')
    return dict(offset=offset, stored_bytes=stored, bytes=unpacked, compression=compression,
                sha1=digest, blocks=blocks, encrypted=bool(flags & 1), deleted=bool(flags & 2),
                block_size=block_size, header_bytes=reader.at-start)


def listing(path, key=None, profile="auto"):
    info = footer(path, profile)
    if info["version"] == 12 and info["profile"] == "snowbreak":
        return listing_snowbreak(path, info, key)
    if info['status'] != 'legacy-readable':
        raise ValueError(f"Cannot list this archive: {info['status']}; version {info['version']}. No offset or key is guessed")
    if info['index_bytes'] > MAX_INDEX: raise ValueError('Index exceeds 128 MiB limit')
    with Path(path).open('rb') as file:
        file.seek(info['index_offset']); data = file.read(info['index_bytes'])
    if hashlib.sha1(data).hexdigest() != info['index_sha1']: raise ValueError('Index SHA-1 mismatch')
    reader = Reader(data)
    mount = reader.string()
    count, = reader.unpack('<i')
    if not 0 <= count <= 1000000: raise ValueError('Invalid entry count')
    entries = []
    for _ in range(count):
        name = reader.string()
        value = entry(reader, info['version'])
        value['name'] = name
        if value['offset'] + value['header_bytes'] + value['stored_bytes'] > info['index_offset']:
            raise ValueError('Entry overlaps index or file end')
        entries.append(value)
    if reader.at != len(data): raise ValueError('Unrecognized trailing index data')
    return dict(archive=info, mount=mount, entries=entries)


def safe_name(name):
    if not name or any(c in name for c in '\\:<>\"|?*') or any(ord(c) < 32 for c in name): raise ValueError('Unsafe archive path')
    path = PurePosixPath(name)
    if not path.parts or path.is_absolute() or '..' in path.parts: raise ValueError('Unsafe archive path')
    if any(part.endswith((' ', '.')) or part.split('.')[0].upper() in
           {'CON', 'PRN', 'AUX', 'NUL', *('COM'+str(i) for i in range(1,10)), *('LPT'+str(i) for i in range(1,10))}
           for part in path.parts): raise ValueError('Unsafe Windows archive path')
    return path


def extract(path, output, pattern='*', key=None, profile='auto'):
    catalog = listing(path, key, profile)
    root = Path(output).resolve()
    if root.exists(): raise ValueError('Output must be a new directory')
    source = Path(path).resolve()
    if source.is_relative_to(root): raise ValueError('Output must not contain source archive')
    selected = [e for e in catalog['entries'] if fnmatch.fnmatchcase(e['name'], pattern)]
    if not selected: raise ValueError('No matching files')
    targets, seen = [], set()
    for item in selected:
        name = safe_name(item['name'])
        target = root.joinpath(*name.parts)
        identity = str(target).casefold()
        if identity in seen or name.as_posix().casefold() == '_pak-extraction.json': raise ValueError('Duplicate or reserved output path')
        seen.add(identity)
        if item['deleted']: raise ValueError('Deleted entries are unsupported')
        if item['encrypted'] and (not key or catalog['archive']['profile'] != 'snowbreak'):
            raise ValueError('Encrypted payload requires Snowbreak profile and key')
        if item['compression'] not in (0, 1) or (item['compression'] and catalog['archive']['version'] >= 8 and item.get('compression_name','').lower() != 'zlib'):
            raise ValueError('Unsupported compression method')
        if (not item['compression'] and item['bytes'] != item['stored_bytes']) or max(item['bytes'], item['stored_bytes']) > MAX_FILE: raise ValueError('Invalid or oversized file')
        targets.append((item, target))
    # All paths/formats checked before creating output. Partial output stays diagnostic on I/O/hash failure.
    root.mkdir(parents=True)
    completed = []
    with source.open('rb') as file:
        for item, target in targets:
            file.seek(item['offset'])
            header = Reader(file.read(item['header_bytes']))
            actual = entry(header, catalog['archive']['version'])
            if any(actual[k] != item[k] for k in ('stored_bytes','bytes','compression','encrypted','blocks')):
                raise ValueError('Entry header differs from index')
            if item.get('sha1') and actual['sha1'] != item['sha1']:
                raise ValueError('Entry hash differs from index')
            item = dict(item, sha1=actual['sha1'])
            if item['compression']:
                pieces, hashed, total, stored = [], hashlib.sha1(), 0, 0
                previous_end = item['offset'] + item['header_bytes']
                for begin, end in item['blocks']:
                    if catalog['archive']['version'] >= 5:
                        begin += item['offset']; end += item['offset']
                    aligned_end = begin + ((end-begin+15)//16*16 if item['encrypted'] else end-begin)
                    if not previous_end <= begin < end <= aligned_end <= catalog['archive']['index_offset']:
                        raise ValueError('Invalid compressed block bounds')
                    if end - begin > MAX_FILE: raise ValueError('Oversized compressed block')
                    file.seek(begin)
                    size = end-begin
                    block = file.read((size+15)//16*16 if item['encrypted'] else size)
                    hashed.update(block); stored += len(block)
                    if len(block) != ((size+15)//16*16 if item['encrypted'] else size): raise ValueError('Truncated payload block')
                    if item['encrypted']: block = snowbreak_crypt(block, Path(path).stem, key)[:size]
                    inflater = zlib.decompressobj()
                    inflated = inflater.decompress(block, item['bytes']-total+1)
                    if not inflater.eof or inflater.unused_data or inflater.unconsumed_tail:
                        raise ValueError('Invalid or oversized zlib stream')
                    total += len(inflated)
                    if total > item['bytes'] or len(inflated) > item['block_size']:
                        raise ValueError('Inflation exceeds declared size')
                    pieces.append(inflated); previous_end = end
                data = b''.join(pieces)
                if stored != item['stored_bytes'] or hashed.hexdigest() != item['sha1']:
                    raise ValueError('Compressed payload SHA-1/size mismatch')
            else:
                size = item['bytes']
                data = file.read((size+15)//16*16 if item['encrypted'] else size)
                if hashlib.sha1(data[:size]).hexdigest() != item['sha1']: raise ValueError('Payload SHA-1 mismatch')
                if item['encrypted']: data = snowbreak_crypt(data, Path(path).stem, key)[:size]
            if len(data) != item['bytes']: raise ValueError('Payload size mismatch')
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open('xb') as out: out.write(data)
            completed.append(dict(item, stored_hash_verified=True, extracted_sha256=hashlib.sha256(data).hexdigest()))
    manifest = root / '_pak-extraction.json'
    if manifest.exists(): raise ValueError('Archive name conflicts with extraction manifest')
    manifest.write_text(json.dumps(dict(source=str(source), archive=catalog['archive'], mount=catalog['mount'], files=completed), indent=2)+'\n')
    return dict(output=str(root), files=len(targets))


def scan(root, profile='auto', key=None):
    rows = []
    for path in sorted(Path(root).rglob('*.pak')):
        try:
            info = footer(path, profile)
            if key and info['version'] == 12 and info['profile'] == 'snowbreak':
                result = listing_snowbreak(path, info, key)
                info = dict(result['archive'], entries=len(result['entries']), mount=result['mount'],
                            encrypted_entries=sum(e['encrypted'] for e in result['entries']))
            rows.append(info)
        except (OSError, ValueError) as error: rows.append(dict(path=str(path), status='error', error=str(error)))
    return dict(status='Host archive evidence; footer interpretation is not extraction acceptance', archives=rows)


def load_key(path):
    text = Path(path).read_text(encoding='utf-8-sig').strip()
    if text.startswith(('0x', '0X')): text = text[2:]
    if len(text) != 64: raise ValueError('Key file must contain exactly 64 hex digits')
    try: return bytes.fromhex(text)
    except ValueError: raise ValueError('Key file must contain exactly 64 hex digits') from None


def snowbreak_crypt(data, stem, key):
    """Snowbreak AES-ECB with filename-derived key. See docs/pak-tool.md provenance."""
    try:
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    except ImportError: raise ValueError('Install cryptography for encrypted Snowbreak archives') from None
    if not key or len(key) != 32: raise ValueError('A 32-byte master key is required')
    if len(data) % 16: raise ValueError('AES input must be block aligned')
    name_hash = hashlib.md5(stem.lower().encode('ascii')).hexdigest().encode('ascii')
    enc = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    derived = enc.update(name_hash) + enc.finalize()
    dec = Cipher(algorithms.AES(derived), modes.ECB()).decryptor()
    return dec.update(data) + dec.finalize()


def index_data(file, info, offset, size, digest, key):
    aligned = (size + 15) // 16 * 16 if info['index_encrypted'] else size
    if not 0 <= size <= MAX_INDEX or not 0 <= offset <= info['footer_offset'] - aligned:
        raise ValueError('Index range exceeds archive bounds or configured limit')
    file.seek(offset); data = file.read(aligned)
    if len(data) != aligned: raise ValueError('Truncated index')
    if info['index_encrypted']: data = snowbreak_crypt(data, Path(info['path']).stem, key)
    data = data[:size]
    if hashlib.sha1(data).hexdigest() != digest:
        raise ValueError('Index SHA-1 mismatch: wrong key, archive filename, profile or corrupt data')
    return data


def checked_count(reader, limit=1000000):
    value, = reader.unpack('<i')
    if not 0 <= value <= limit: raise ValueError('Invalid index count')
    return value


def encoded_entry(data, at, info):
    if not 0 <= at < len(data): raise ValueError('Encoded entry reference outside entry blob')
    reader = Reader(data); reader.at = at
    bits, = reader.unpack('<I')
    block_size = reader.unpack('<I')[0] if bits & 63 == 63 else (bits & 63) << 11
    compression = (bits >> 23) & 63
    offset = reader.unpack('<I' if bits & (1 << 31) else '<q')[0] ^ 0x1F1E1D1C
    unpacked = reader.unpack('<I' if bits & (1 << 30) else '<q')[0]
    stored = reader.unpack('<I' if bits & (1 << 29) else '<q')[0] if compression else unpacked
    encrypted = bool(bits & (1 << 22))
    count = (bits >> 6) & 65535
    header_size = 53 + (4 + 16 * count if compression else 0)
    blocks = []
    # Keep block locations relative, consistent with the serialized entry header.
    cursor = header_size
    if count == 1 and not encrypted:
        blocks = [(cursor, cursor + stored)]
    elif count:
        for _ in range(count):
            length, = reader.unpack('<I')
            blocks.append((cursor, cursor + length))
            cursor += (length + 15) // 16 * 16 if encrypted else length
    if count == 1: block_size = unpacked
    if not count: block_size = 0
    methods = ['None'] + info['compression_methods']
    if compression >= len(methods) or (compression and not methods[compression]):
        raise ValueError('Unknown compression method index')
    return dict(offset=offset, stored_bytes=stored, bytes=unpacked, compression=compression,
                compression_name=methods[compression], sha1=None, blocks=blocks,
                encrypted=encrypted, deleted=False, block_size=block_size, header_bytes=header_size)


def listing_snowbreak(path, info, key):
    if not info['index_bounds_valid']: raise ValueError('Snowbreak index bounds invalid after XOR')
    if info['index_encrypted'] and not key: raise ValueError('Encrypted Snowbreak index requires --key-file')
    with Path(path).open('rb') as file:
        primary = index_data(file, info, info['index_offset'], info['index_bytes'], info['index_sha1'], key)
        reader = Reader(primary)
        mount = reader.string(); file_count = checked_count(reader)
        seed, = reader.unpack('<Q')
        secondary = []
        for _ in range(2):
            present, = reader.unpack('<I')
            if present not in (0, 1): raise ValueError('Invalid secondary index flag')
            secondary.append((*reader.unpack('<qq'), reader.take(20).hex()) if present else None)
        blob = reader.take(checked_count(reader, MAX_INDEX))
        nonencoded = [entry(reader, 12) for _ in range(checked_count(reader))]
        # UE serializes encrypted-index padding into the hashed index size.
        if len(reader.data)-reader.at > 15: raise ValueError('Unexpected primary index trailing bytes')
        if secondary[0]:
            path_hash = index_data(file, info, *secondary[0], key)
            hashes = Reader(path_hash); hash_count = checked_count(hashes)
            for _ in range(hash_count):
                _, reference = hashes.unpack('<Qi')
                if reference >= len(blob) or (reference < 0 and reference != -2147483648 and -reference-1 >= len(nonencoded)):
                    raise ValueError('Invalid path-hash reference')
            if hash_count != file_count: raise ValueError('Path-hash count differs from primary count')
        if not secondary[1]: raise ValueError('Archive omits full directory index; cannot recover names')
        directory = index_data(file, info, *secondary[1], key)
        reader = Reader(directory); entries = []; seen = set(); deleted = 0
        for _ in range(checked_count(reader)):
            folder = reader.string()
            for _ in range(checked_count(reader)):
                file_name = reader.string()
                name = (folder[1:] if mount.endswith('/') and folder.startswith('/') else folder) + file_name
                reference, = reader.unpack('<i')
                if reference == -2147483648:
                    deleted += 1; continue
                if reference >= 0: value = encoded_entry(blob, reference, info)
                elif 0 <= -reference-1 < len(nonencoded): value = dict(nonencoded[-reference-1])
                else: raise ValueError('Invalid nonencoded entry reference')
                safe_name(name)
                if name.casefold() in seen: raise ValueError('Duplicate directory entry')
                seen.add(name.casefold()); value['name'] = name
                disk_size = (value['stored_bytes']+15)//16*16 if value['encrypted'] and not value['compression'] else value['stored_bytes']
                if min(value['offset'], value['bytes'], value['stored_bytes']) < 0 or value['offset'] + value['header_bytes'] + disk_size > info['index_offset']:
                    raise ValueError('Entry overlaps primary index or archive end')
                entries.append(value)
        if len(entries) + deleted != file_count: raise ValueError('Directory count differs from primary count')
        if len(reader.data)-reader.at > 15: raise ValueError('Unexpected directory index trailing bytes')
    return dict(archive=dict(info, status='snowbreak-index-verified', index_hashes_verified=True), mount=mount,
                path_hash_seed=seed, deleted_entries=deleted, entries=entries)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    for name in ('inspect', 'list'):
        command = sub.add_parser(name); command.add_argument('path', type=Path); command.add_argument('--out', type=Path)
    command = sub.add_parser('scan'); command.add_argument('path', type=Path); command.add_argument('--out', type=Path, required=True)
    command = sub.add_parser('extract'); command.add_argument('path', type=Path); command.add_argument('output', type=Path); command.add_argument('--glob', default='*')
    for command in sub.choices.values():
        command.add_argument('--profile', choices=('auto','snowbreak','standard'), default='auto', help='Auto is Snowbreak v12 or standard legacy; use standard for other games')
        command.add_argument('--key-file', type=Path, help='Local file containing a 32-byte hex master key')
    args = parser.parse_args()
    try:
        key = load_key(args.key_file) if args.key_file else None
        result = (footer(args.path, args.profile) if args.command == 'inspect' else listing(args.path, key, args.profile) if args.command == 'list'
                  else scan(args.path, args.profile, key) if args.command == 'scan' else extract(args.path, args.output, args.glob, key, args.profile))
        if getattr(args, 'out', None):
            args.out.parent.mkdir(parents=True, exist_ok=True)
            with args.out.open('x', encoding='utf-8') as file: json.dump(result, file, indent=2); file.write('\n')
            print(f'Saved {args.out}')
        else: print(json.dumps(result, indent=2))
    except (OSError, ValueError, UnicodeError, struct.error, zlib.error) as error: parser.exit(1, f'PAK: {error}\n')


if __name__ == '__main__': main()
