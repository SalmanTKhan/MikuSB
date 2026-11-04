"""Read-only PE metadata and bounded string searches, without full analysis.

Requires pefile. Does not load the executable into a process or modify it.
"""
import argparse
import hashlib
import json
from pathlib import Path

import pefile


def inspect(path):
    data = path.read_bytes()
    pe = pefile.PE(data=data, fast_load=True)
    pe.parse_data_directories(directories=[1])  # Imports only; skip resource traversal.
    base = pe.OPTIONAL_HEADER.ImageBase
    sections = [dict(name=s.Name.rstrip(b'\0').decode(errors='replace'),
                     va=hex(base + s.VirtualAddress), raw_bytes=s.SizeOfRawData,
                     virtual_bytes=s.Misc_VirtualSize, entropy=round(s.get_entropy(), 3),
                     flags=hex(s.Characteristics)) for s in pe.sections]
    imports = [dict(dll=d.dll.decode(errors='replace'), symbols=[
        dict(name=i.name.decode(errors='replace') if i.name else str(i.ordinal),
             va=hex(i.address)) for i in d.imports])
        for d in getattr(pe, 'DIRECTORY_ENTRY_IMPORT', [])]
    anchors = []
    for term in ('https://', 'http://', 'amazingseasun', 'seasungames', 'snowbreak',
                 'ALL_PROXY', 'ReqLogin', 'ReqCallGS', 'dispatch', 'CURLOPT', 'SSL_VERIFY'):
        for encoding in ('ascii', 'utf-16le'):
            needle = term.encode(encoding)
            start = 0
            for _ in range(8):
                offset = data.find(needle, start)
                if offset < 0:
                    break
                try:
                    rva = pe.get_rva_from_offset(offset)
                except pefile.PEFormatError:
                    rva = None
                va = hex(base + rva) if rva is not None else None
                unit = 2 if encoding == 'utf-16le' else 1
                end = offset
                while end < min(len(data), offset + 240) and data[end:end+unit] != bytes(unit):
                    end += unit
                anchors.append(dict(term=term, encoding=encoding, va=va, file_offset=hex(offset),
                                    text=data[offset:end].decode(encoding, errors='replace')))
                start = offset + len(needle)
    return dict(sha256=hashlib.sha256(data).hexdigest(), bytes=len(data),
                image_base=hex(base), entry_va=hex(base + pe.OPTIONAL_HEADER.AddressOfEntryPoint),
                sections=sections, imports=imports, anchors=anchors,
                limits='Static file metadata only. Entropy and missing strings do not prove packing.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('exe', type=Path)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    report = inspect(args.exe)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(sha256=report['sha256'], entry_va=report['entry_va'],
                          sections=len(report['sections']),
                          imports=sum(len(d['symbols']) for d in report['imports']),
                          anchor_matches=len(report['anchors']), report=str(args.out)), indent=2))
