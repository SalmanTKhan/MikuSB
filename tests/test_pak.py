import hashlib
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

spec = importlib.util.spec_from_file_location('snowbreak_pak', Path(__file__).resolve().parents[1]/'tools/client/pak.py')
pak = importlib.util.module_from_spec(spec); spec.loader.exec_module(pak)


def string(value):
    raw = value.encode()+b'\0'
    return struct.pack('<i',len(raw))+raw


def fixture(path, name='data/test.txt', compressed=False):
    plain = b'preservation fixture' * 20
    chunks = [zlib.compress(plain[:200]),zlib.compress(plain[200:])] if compressed else [plain]
    stored = b''.join(chunks)
    header_size = 53 + (4+16*len(chunks) if compressed else 0)
    blocks = []; at = header_size
    for chunk in chunks: blocks.append((at,at+len(chunk))); at+=len(chunk)
    header = struct.pack('<qqqI',0,len(stored),len(plain),int(compressed))+hashlib.sha1(stored).digest()
    if compressed: header += struct.pack('<i',len(blocks))+b''.join(struct.pack('<qq',*b) for b in blocks)
    header += struct.pack('<BI',0,200 if compressed else 0)
    index=string('../../../Game/Content/')+struct.pack('<i',1)+string(name)+header
    footer=pak.MAGIC+struct.pack('<iqq',3,len(header)+len(stored),len(index))+hashlib.sha1(index).digest()
    path.write_bytes(header+stored+index+footer)
    return plain


class PakTests(unittest.TestCase):
    def test_extract_both_methods(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            for compressed in (False,True):
                source=root/f'{compressed}.pak'; expected=fixture(source,compressed=compressed)
                output=root/f'out{compressed}'
                self.assertEqual(pak.extract(source,output)['files'],1)
                self.assertEqual((output/'data/test.txt').read_bytes(),expected)
                with self.assertRaises(ValueError):pak.extract(source,output)
    def test_index_hash(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'bad.pak';fixture(p);raw=bytearray(p.read_bytes());raw[-1]^=1;p.write_bytes(raw)
            with self.assertRaisesRegex(ValueError,'Index SHA'):pak.listing(p)
    def test_payload_hash(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'bad.pak';fixture(p);raw=bytearray(p.read_bytes());raw[53]^=1;p.write_bytes(raw)
            with self.assertRaisesRegex(ValueError,'Payload SHA'):pak.extract(p,Path(temp)/'out')
    def test_paths(self):
        for name in ('../escape','/absolute','C:/escape','a\\b','NUL.txt','folder/end.','.', 'a:stream', 'a?b'):
            with self.subTest(name=name),self.assertRaises(ValueError):pak.safe_name(name)
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'bad.pak';fixture(p,'../escape')
            output=Path(temp)/'out'
            with self.assertRaises(ValueError):pak.extract(p,output)
            self.assertFalse(output.exists())
    def test_unresolved_encrypted_footer(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'retail.pak'
            p.write_bytes(b'\0'*16+b'\1'+pak.MAGIC+struct.pack('<iqq',12,0x1c000000,32)+b'\0'*20+b'\0'*160)
            self.assertEqual(pak.footer(p)['status'],'index-offset-unresolved')
            with self.assertRaisesRegex(ValueError,'bounds invalid after XOR'):pak.listing(p)
    def test_truncated(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'bad.pak';p.write_bytes(pak.MAGIC)
            with self.assertRaises(ValueError):pak.footer(p)
    def test_inflation_bound(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'bad.pak';fixture(p,compressed=True)
            raw=bytearray(p.read_bytes());info=pak.footer(p)
            # Match malicious tiny output sizes in both header and index; refresh index hash.
            struct.pack_into('<q',raw,16,1)
            index_start=info['index_offset']; header_at=index_start+len(string('../../../Game/Content/'))+4+len(string('data/test.txt'))
            struct.pack_into('<q',raw,header_at+16,1)
            index=raw[index_start:index_start+info['index_bytes']]
            raw[-20:]=hashlib.sha1(index).digest();p.write_bytes(raw)
            with self.assertRaisesRegex(ValueError,'oversized|Inflation'):pak.extract(p,Path(temp)/'out')


if __name__=='__main__':unittest.main()
