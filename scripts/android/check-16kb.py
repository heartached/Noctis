#!/usr/bin/env python3
"""Checks Android packages for 16 KB page-size compatibility.

Google Play rejects apps targeting Android 15+ whose native libraries are not 16 KB
page-size compatible. Two things must hold for every lib/<abi>/*.so:

  1. ELF: every PT_LOAD segment has p_align >= 16384 (0x4000). 32-bit ABIs
     (armeabi-v7a, x86) are exempt: Android only runs 16 KB pages on 64-bit.
  2. ZIP (APK only): a .so stored *uncompressed* must start at a file offset that
     is a multiple of 16384, or the loader cannot mmap it in place. Compressed
     .so files are extracted at install time, so their offset does not matter.

Accepts .aab, .apk, .apks (bundletool output, APKs nested inside) and directories
(checked recursively for those three). Exit code 0 = all compliant, 1 = a violation,
2 = usage error / nothing to check. Standard library only.
"""
import io
import os
import struct
import sys
import zipfile

PAGE = 16384
EXEMPT_ABIS = {"armeabi-v7a", "armeabi", "x86"}


def min_load_align(data):
    """Smallest p_align over PT_LOAD segments, or None if not an ELF file."""
    if data[:4] != b"\x7fELF":
        return None
    is64 = data[4] == 2
    little = data[5] == 1
    e = "<" if little else ">"
    if is64:
        phoff = struct.unpack_from(e + "Q", data, 0x20)[0]
        phentsize, phnum = struct.unpack_from(e + "HH", data, 0x36)
    else:
        phoff = struct.unpack_from(e + "I", data, 0x1C)[0]
        phentsize, phnum = struct.unpack_from(e + "HH", data, 0x2A)
    aligns = []
    for i in range(phnum):
        off = phoff + i * phentsize
        p_type = struct.unpack_from(e + "I", data, off)[0]
        if p_type != 1:  # PT_LOAD
            continue
        if is64:
            p_align = struct.unpack_from(e + "Q", data, off + 48)[0]
        else:
            p_align = struct.unpack_from(e + "I", data, off + 28)[0]
        aligns.append(p_align)
    return min(aligns) if aligns else 0


def abi_of(name):
    parts = name.split("/")
    return parts[parts.index("lib") + 1] if "lib" in parts and parts.index("lib") + 1 < len(parts) else "?"


def data_offset(zf_bytes, info):
    """File offset of an entry's data: local header + name + extra lengths."""
    base = info.header_offset
    n, x = struct.unpack_from("<HH", zf_bytes, base + 26)
    return base + 30 + n + x


def check_zip(label, raw, is_apk, problems, counts):
    zf = zipfile.ZipFile(io.BytesIO(raw))
    for info in zf.infolist():
        name = info.filename
        if name.endswith(".apk"):
            check_zip(label + "!" + name, zf.read(info), True, problems, counts)
            continue
        if not name.endswith(".so") or "/lib/" not in "/" + name:
            continue
        abi = abi_of(name)
        data = zf.read(info)
        align = min_load_align(data)
        counts["so"] += 1
        if align is None:
            continue  # not ELF (never seen in practice; ignore rather than fail)
        if abi not in EXEMPT_ABIS and align < PAGE:
            problems.append(f"{label}!{name}: ELF LOAD p_align {align:#x} < 0x4000")
        if is_apk and info.compress_type == zipfile.ZIP_STORED:
            counts["stored"] += 1
            off = data_offset(raw, info)
            if abi not in EXEMPT_ABIS and off % PAGE != 0:
                problems.append(f"{label}!{name}: stored uncompressed at offset {off} (not 16 KB aligned)")


def targets(paths):
    for p in paths:
        if os.path.isdir(p):
            for root, _, files in os.walk(p):
                for f in files:
                    if f.endswith((".aab", ".apk", ".apks")):
                        yield os.path.join(root, f)
        else:
            yield p


def main(argv):
    files = list(targets(argv[1:]))
    if not files:
        print("usage: check-16kb.py <file.aab|file.apk|file.apks|dir> ...")
        return 2
    failed = False
    for f in files:
        problems, counts = [], {"so": 0, "stored": 0}
        with open(f, "rb") as fh:
            raw = fh.read()
        check_zip(os.path.basename(f), raw, f.endswith(".apk"), problems, counts)
        status = "FAIL" if problems else "OK"
        print(f"{status}  {f}: {counts['so']} .so checked, {counts['stored']} stored uncompressed")
        for p in problems:
            print("   " + p)
        failed |= bool(problems)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
