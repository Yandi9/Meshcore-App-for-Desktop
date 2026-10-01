#!/usr/bin/env python3
"""Checks an .msix the way Windows does before trusting its signature: every file in AppxBlockMap.xml is present,
its local header size matches LfhSize and every 64 KiB block matches its hash. usage: check_msix.py <file.msix>"""
import base64
import hashlib
import struct
import sys
import urllib.parse
import zipfile
import xml.etree.ElementTree as ET

path = sys.argv[1]
z = zipfile.ZipFile(path)
raw = open(path, "rb").read()
ns = {"b": "http://schemas.microsoft.com/appx/2010/blockmap"}
bm = ET.fromstring(z.read("AppxBlockMap.xml"))
by_name = {urllib.parse.unquote(i.filename): i for i in z.infolist()}
problems = 0
for f in bm.findall("b:File", ns):
    name = f.get("Name").replace("\\", "/")
    info = by_name.get(name)
    if info is None:
        print("missing", name); problems += 1; continue
    nl, el = struct.unpack("<HH", raw[info.header_offset + 26:info.header_offset + 30])
    if 30 + nl + el != int(f.get("LfhSize")):
        print("LfhSize mismatch", name); problems += 1
    data = z.read(info)
    if len(data) != int(f.get("Size")):
        print("size mismatch", name); problems += 1
    blocks = f.findall("b:Block", ns)
    for i, b in enumerate(blocks):
        if base64.b64encode(hashlib.sha256(data[i * 65536:(i + 1) * 65536]).digest()).decode() != b.get("Hash"):
            print("hash mismatch", name, i); problems += 1
    if len(blocks) != (len(data) + 65535) // 65536:
        print("block count mismatch", name); problems += 1
listed = {f.get("Name").replace("\\", "/") for f in bm.findall("b:File", ns)}
extra = set(by_name) - listed - {"AppxBlockMap.xml", "[Content_Types].xml", "AppxSignature.p7x", "AppxMetadata/CodeIntegrity.cat"}
if extra:
    print("files not in the block map:", extra); problems += 1
print("OK" if problems == 0 else f"{problems} problem(s)", path, [i.filename for i in z.infolist()])
sys.exit(1 if problems else 0)
