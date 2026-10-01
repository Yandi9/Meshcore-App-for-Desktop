#!/usr/bin/env python3
"""
Packs a folder into an unsigned .msix the way MakeAppx does (zip64 layout, data descriptors, AppxBlockMap.xml with
SHA-256 per 64 KiB block, [Content_Types].xml). Payload files are stored; sign the result with osslsigncode.

usage: pack_msix.py <layout folder containing AppxManifest.xml> <out.msix>
"""
import base64
import hashlib
import os
import struct
import sys
import time
import zlib
from xml.sax.saxutils import quoteattr

BLOCK = 65536
CONTENT_TYPES = {
    "exe": "application/x-msdownload",
    "dll": "application/x-msdownload",
    "png": "image/png",
    "json": "application/json",
    "xml": "application/vnd.ms-appx.manifest+xml",
}


def dos_time(t):
    lt = time.localtime(t)
    return ((lt.tm_hour << 11) | (lt.tm_min << 5) | (lt.tm_sec // 2),
            ((lt.tm_year - 1980) << 9) | (lt.tm_mon << 5) | lt.tm_mday)


def zip_name(rel):
    safe = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~/[]"
    return "".join(c if c in safe else "%{:02X}".format(ord(c)) for c in rel)


class Writer:
    def __init__(self, path):
        self.f = open(path, "wb")
        self.entries = []
        self.mtime, self.mdate = dos_time(time.time())

    def add(self, name, data, deflate, descriptor):
        offset = self.f.tell()
        crc = zlib.crc32(data) & 0xFFFFFFFF
        if deflate:
            c = zlib.compressobj(9, zlib.DEFLATED, -15)
            payload = c.compress(data) + c.flush()
        else:
            payload = data
        method = 8 if deflate else 0
        version = 45 if descriptor else 20
        flag = 0x08 if descriptor else 0
        raw = name.encode("utf-8")
        if descriptor:
            header = struct.pack("<IHHHHHIIIHH", 0x04034B50, version, flag, method, self.mtime, self.mdate, 0, 0, 0, len(raw), 0)
        else:
            header = struct.pack("<IHHHHHIIIHH", 0x04034B50, version, flag, method, self.mtime, self.mdate, crc, len(payload), len(data), len(raw), 0)
        self.f.write(header + raw + payload)
        if descriptor:
            self.f.write(struct.pack("<IIQQ", 0x08074B50, crc, len(payload), len(data)))
        self.entries.append((raw, version, flag, method, crc, len(payload), len(data), offset))
        return 30 + len(raw)

    def close(self):
        cd_start = self.f.tell()
        for raw, version, flag, method, crc, csize, usize, offset in self.entries:
            self.f.write(struct.pack("<IHHHHHHIIIHHHHHII", 0x02014B50, 45, version, flag, method, self.mtime, self.mdate,
                                     crc, csize, usize, len(raw), 0, 0, 0, 0, 0, offset) + raw)
        cd_end = self.f.tell()
        n = len(self.entries)
        self.f.write(struct.pack("<IQHHIIQQQQ", 0x06064B50, 44, 45, 45, 0, 0, n, n, cd_end - cd_start, cd_start))
        self.f.write(struct.pack("<IIQI", 0x07064B50, 0, cd_end, 1))
        self.f.write(struct.pack("<IHHHHIIH", 0x06054B50, 0, 0, 0xFFFF, 0xFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0))
        self.f.close()


def main(layout, out):
    files = []
    for root, _, names in os.walk(layout):
        for n in names:
            rel = os.path.relpath(os.path.join(root, n), layout).replace(os.sep, "/")
            if rel in ("AppxManifest.xml", "AppxBlockMap.xml", "[Content_Types].xml", "AppxSignature.p7x"):
                continue
            files.append(rel)
    files.sort(key=lambda r: (r.count("/"), r.lower()))
    files.append("AppxManifest.xml")

    w = Writer(out)
    blockmap = ['<?xml version="1.0" encoding="UTF-8" standalone="no"?>',
                '<BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap" HashMethod="http://www.w3.org/2001/04/xmlenc#sha256">']
    extensions = set()
    for rel in files:
        with open(os.path.join(layout, rel), "rb") as fh:
            data = fh.read()
        lfh = w.add(zip_name(rel), data, deflate=False, descriptor=True)
        blocks = "".join('<Block Hash="{}"/>'.format(base64.b64encode(hashlib.sha256(data[i:i + BLOCK]).digest()).decode())
                         for i in range(0, len(data), BLOCK))
        blockmap.append('<File Name={} Size="{}" LfhSize="{}">{}</File>'.format(quoteattr(rel.replace("/", "\\")), len(data), lfh, blocks))
        ext = rel.rsplit(".", 1)[-1].lower() if "." in rel else ""
        if ext:
            extensions.add(ext)
    blockmap.append("</BlockMap>")
    w.add("AppxBlockMap.xml", "".join(blockmap).encode("utf-8"), deflate=True, descriptor=True)

    types = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
             '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">']
    for ext in sorted(extensions):
        types.append('<Default Extension="{}" ContentType="{}"/>'.format(ext, CONTENT_TYPES.get(ext, "application/octet-stream")))
    types.append('<Override PartName="/AppxBlockMap.xml" ContentType="application/vnd.ms-appx.blockmap+xml"/>')
    types.append("</Types>")
    w.add("[Content_Types].xml", "".join(types).encode("utf-8"), deflate=True, descriptor=False)
    w.close()
    print("packed", out, os.path.getsize(out), "bytes,", len(files), "files")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
