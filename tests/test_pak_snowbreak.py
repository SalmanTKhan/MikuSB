import hashlib
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

from test_pak import pak, string

KEY = bytes(range(32))


def fixture(path, compressed=True, payload_size=70000):
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    encoder=Cipher(algorithms.AES(KEY), modes.ECB()).encryptor()
    derived=encoder.update(hashlib.md5(path.stem.lower().encode()).hexdigest().encode())+encoder.finalize()
    def encrypt(data):
        # Include deliberately nonzero padding in hashed regions.
        padded=data+b'P'*((-len(data))%16)
        enc=Cipher(algorithms.AES(derived),modes.ECB()).encryptor()
        return enc.update(padded)+enc.finalize()
    plain=(bytes(range(251))*((payload_size+250)//251))[:payload_size]
    parts=[zlib.compress(plain[i:i+32768]) for i in range(0,len(plain),32768)] if compressed else [plain]
    encrypted=[encrypt(p) for p in parts]
    stored=b''.join(encrypted)
    count=len(parts) if compressed else 0
    header_size=53+(4+16*count if compressed else 0)
    blocks=[];cursor=header_size
    for raw,cipher in zip(parts,encrypted):blocks.append((cursor,cursor+len(raw)));cursor+=len(cipher)
    header=struct.pack('<qqqI',0,len(stored) if compressed else len(plain),len(plain),int(compressed))+hashlib.sha1(stored if compressed else stored[:len(plain)]).digest()
    if compressed:header+=struct.pack('<i',count)+b''.join(struct.pack('<qq',*b) for b in blocks)
    header+=struct.pack('<BI',1,32768 if compressed else 0)
    bits=(1<<31)|(1<<30)|(1<<29)|(1<<22)|(int(compressed)<<23)|(count<<6)|(16 if compressed else 0)
    blob=struct.pack('<III',bits,0x1F1E1D1C,len(plain))
    if compressed:blob+=struct.pack('<I',len(stored))+b''.join(struct.pack('<I',len(p)) for p in parts)
    directory=struct.pack('<i',1)+string('/')+struct.pack('<i',1)+string('sample.bin')+struct.pack('<i',0)
    directory+=b'Q'*((-len(directory))%16)
    hashes=struct.pack('<iQi',1,0x123456789,0)
    mount=string('../../../Game/Content/')
    primary_size=len(mount)+12+80+4+len(blob)+4
    primary_size=(primary_size+15)//16*16
    offset=len(header)+len(stored)
    hash_offset=offset+primary_size;directory_offset=hash_offset+len(encrypt(hashes))
    primary=mount+struct.pack('<iQ',1,123)
    for off,data in ((hash_offset,hashes),(directory_offset,directory)):
        data=data+b'P'*((-len(data))%16)
        primary+=struct.pack('<Iqq',1,off,len(data))+hashlib.sha1(data).digest()
    primary+=struct.pack('<i',len(blob))+blob+struct.pack('<i',0)
    primary+=b'P'*((-len(primary))%16)
    footer=b'\0'*16+b'\1'+pak.MAGIC+struct.pack('<iqq',12,offset^0x1C1D1E1F,len(primary))+hashlib.sha1(primary).digest()
    footer+=b'Zlib'+b'\0'*156
    path.write_bytes(header+stored+encrypt(primary)+encrypt(hashes)+encrypt(directory)+footer)
    return plain,directory_offset


@unittest.skipUnless(importlib.util.find_spec('cryptography'),'cryptography required')
class SnowbreakPakTests(unittest.TestCase):
    def test_encrypted_roundtrip_multiblock_and_uncompressed(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp)
            for compressed in (False,True):
                source=root/f'Archive{compressed}.pak';expected,_=fixture(source,compressed)
                info=pak.footer(source)
                self.assertEqual(info['stored_index_offset']^0x1C1D1E1F,info['index_offset'])
                listing=pak.listing(source,KEY)
                self.assertTrue(listing['archive']['index_hashes_verified'])
                self.assertEqual(listing['entries'][0]['offset'],0)
                output=root/f'out{compressed}';pak.extract(source,output,key=KEY)
                self.assertEqual((output/'sample.bin').read_bytes(),expected)
    def test_wrong_key_and_missing_key(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'archive.pak';fixture(p)
            with self.assertRaisesRegex(ValueError,'requires --key-file'):pak.listing(p)
            with self.assertRaisesRegex(ValueError,'Index SHA-1 mismatch'):pak.listing(p,b'X'*32)
    def test_renaming_archive_changes_key(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'archive.pak';fixture(p);p=p.rename(Path(temp)/'renamed.pak')
            with self.assertRaisesRegex(ValueError,'Index SHA-1 mismatch'):pak.listing(p,KEY)
    def test_directory_hash_corruption(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'archive.pak';_,offset=fixture(p);raw=bytearray(p.read_bytes());raw[offset]^=1;p.write_bytes(raw)
            with self.assertRaisesRegex(ValueError,'Index SHA-1 mismatch'):pak.listing(p,KEY)
    def test_ciphertext_payload_hash_corruption(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'archive.pak';fixture(p,False);raw=bytearray(p.read_bytes());raw[53]^=1;p.write_bytes(raw)
            with self.assertRaisesRegex(ValueError,'Payload SHA-1 mismatch'):pak.extract(p,Path(temp)/'out',key=KEY)
    def test_standard_profile_does_not_apply_snowbreak_transform(self):
        with tempfile.TemporaryDirectory() as temp:
            p=Path(temp)/'archive.pak';fixture(p)
            self.assertEqual(pak.footer(p,'standard')['offset_transform'],'none')
            self.assertFalse(pak.footer(p,'standard')['index_bounds_valid'])


if __name__=='__main__':unittest.main()
