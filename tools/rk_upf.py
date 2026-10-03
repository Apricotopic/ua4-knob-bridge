#!/usr/bin/env python3
"""Inspect and unpack Sinowealth BLESINO UPF firmware containers.

The TEA transform mirrors PH Update Tool 1.30 functions 0x00404820 and
0x00404910. The four trailer bytes are treated as four zero-extended TEA
DWORD keys. Firmware DWORDs are serialized big-endian in each 8-byte block.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import struct


MASK32 = 0xFFFFFFFF
# PH Update Tool 1.30 does not use the standard TEA constants.  Its decryptor
# at 0x00404945 starts with 0xC6ED3720 and advances the sum by 0x61C89647 at
# 0x00404980.  The inverse encryption step is therefore 0x9E3769B9.
DECRYPT_SUM_INITIAL = 0xC6ED3720
DECRYPT_SUM_STEP = 0x61C89647
ENCRYPT_SUM_STEP = 0x9E3769B9
HEADER_SIZE = 0x80
SEGMENT_LAYOUT = {
    "usb_main": (0x26, 0x2E),
    "ble": (0x2A, 0x32),
    "slave": (0x3C, 0x40),
}


def u32(value: int) -> int:
    return value & MASK32


def tea_mix(value: int, key_left: int, key_right: int, total: int) -> int:
    """Reproduce the updater's 32-bit register arithmetic exactly."""
    left = u32((value << 4) + key_left)
    right = u32((value >> 5) + key_right)
    return u32(left ^ right ^ u32(total + value))


def tea_decrypt_block(block: bytes, key_bytes: bytes) -> bytes:
    if len(block) != 8 or len(key_bytes) != 4:
        raise ValueError("TEA requires an 8-byte block and four key bytes")
    v0, v1 = struct.unpack(">II", block)
    k0, k1, k2, k3 = key_bytes
    total = DECRYPT_SUM_INITIAL
    for _ in range(32):
        v1 = u32(v1 - tea_mix(v0, k2, k3, total))
        v0 = u32(v0 - tea_mix(v1, k0, k1, total))
        total = u32(total + DECRYPT_SUM_STEP)
    return struct.pack(">II", v0, v1)


def tea_encrypt_block(block: bytes, key_bytes: bytes) -> bytes:
    if len(block) != 8 or len(key_bytes) != 4:
        raise ValueError("TEA requires an 8-byte block and four key bytes")
    v0, v1 = struct.unpack(">II", block)
    k0, k1, k2, k3 = key_bytes
    total = 0
    for _ in range(32):
        total = u32(total + ENCRYPT_SUM_STEP)
        # PH Update Tool decrypts v1 and then v0 with the same pre-decrement
        # sum. Reversing that operation increments sum first, then restores
        # v0 followed by v1.
        v0 = u32(v0 + tea_mix(v1, k0, k1, total))
        v1 = u32(v1 + tea_mix(v0, k2, k3, total))
    return struct.pack(">II", v0, v1)


def transform_region(region: bytes, key: bytes, decrypt: bool) -> bytes:
    if len(region) % 8:
        raise ValueError(f"region length 0x{len(region):X} is not an 8-byte multiple")
    transform = tea_decrypt_block if decrypt else tea_encrypt_block
    return b"".join(transform(region[index:index + 8], key) for index in range(0, len(region), 8))


def crc16_ccitt(data: bytes) -> int:
    """CRC-16/CCITT with polynomial 0x1021 and initial value zero.

    This matches PH Update Tool 1.30 function 0x004047C0 and the three
    checksums stored in the official M75 UPF header.
    """
    crc = 0
    for value in data:
        crc ^= value << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x1021) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def c_string(data: bytes) -> str:
    return data.split(b"\0", 1)[0].decode("ascii", "replace")


def parse_container(data: bytes) -> tuple[dict[str, object], list[tuple[str, bytes]]]:
    if len(data) < HEADER_SIZE + 4 or data[:2] != b"\x5A\xA5" or data[2:9] != b"BLESINO":
        raise ValueError("not a supported BLESINO UPF")
    kind = data[9]
    if kind != 3:
        raise ValueError(f"this RK-specific parser currently expects type 3, got {kind}")
    sizes = [int.from_bytes(data[0x26:0x2A], "big"),
             int.from_bytes(data[0x2A:0x2E], "big"),
             int.from_bytes(data[0x3C:0x40], "big")]
    names = ["usb_main", "ble", "slave"]
    expected = HEADER_SIZE + sum(sizes) + 4
    if expected != len(data):
        raise ValueError(f"header lengths imply 0x{expected:X} bytes, file has 0x{len(data):X}")
    key = data[-4:]
    segments: list[tuple[str, bytes]] = []
    cursor = HEADER_SIZE
    for name, size in zip(names, sizes, strict=True):
        encrypted = data[cursor:cursor + size]
        segments.append((name, transform_region(encrypted, key, decrypt=True)))
        cursor += size
    metadata: dict[str, object] = {
        "format_magic": data[:9].hex(" ").upper(),
        "container_type": kind,
        "device_family": c_string(data[0x0A:0x11]),
        "vid": f"{int.from_bytes(data[0x11:0x13], 'big'):04X}",
        "pid": f"{int.from_bytes(data[0x13:0x15], 'big'):04X}",
        "header_word_15": f"{int.from_bytes(data[0x15:0x17], 'big'):04X}",
        "header_word_17": f"{int.from_bytes(data[0x17:0x19], 'big'):04X}",
        "header_byte_19": data[0x19],
        "usb_version": c_string(data[0x1A:0x20]),
        "ble_version": c_string(data[0x20:0x26]),
        "slave_version": c_string(data[0x36:0x3C]),
        "segment_sizes": dict(zip(names, sizes, strict=True)),
        "segment_checksums_header": {
            "usb_main": data[0x2E:0x30].hex().upper(),
            "ble": data[0x32:0x34].hex().upper(),
            "slave": data[0x40:0x42].hex().upper(),
        },
        "segment_checksums_computed": {
            name: f"{crc16_ccitt(segment):04X}" for name, segment in segments
        },
        "chip_signature": data[0x4E:0x52].hex().upper(),
        "header_bytes_52_55": data[0x52:0x56].hex().upper(),
        "header_size_marker": data[0x56],
        "tea_key": key.hex().upper(),
        "file_sha256": hashlib.sha256(data).hexdigest().upper(),
    }
    stored_checksums = {
        "usb_main": int.from_bytes(data[0x2E:0x30], "big"),
        "ble": int.from_bytes(data[0x32:0x34], "big"),
        "slave": int.from_bytes(data[0x40:0x42], "big"),
    }
    metadata["segment_checksums_valid"] = {
        name: stored_checksums[name] == crc16_ccitt(segment) for name, segment in segments
    }
    return metadata, segments


def pack_container(original: bytes, replacements: dict[str, bytes] | None = None) -> bytes:
    """Rebuild a UPF while preserving all unknown header bytes and its TEA key."""
    _, parsed = parse_container(original)
    segments = dict(parsed)
    for name, replacement in (replacements or {}).items():
        if name not in SEGMENT_LAYOUT:
            raise ValueError(f"unknown segment {name!r}")
        if len(replacement) % 8:
            raise ValueError(f"replacement {name} length is not an 8-byte multiple")
        segments[name] = replacement

    header = bytearray(original[:HEADER_SIZE])
    key = original[-4:]
    payload = bytearray()
    for name in SEGMENT_LAYOUT:
        segment = segments[name]
        size_offset, checksum_offset = SEGMENT_LAYOUT[name]
        header[size_offset:size_offset + 4] = len(segment).to_bytes(4, "big")
        header[checksum_offset:checksum_offset + 2] = crc16_ccitt(segment).to_bytes(2, "big")
        payload.extend(transform_region(segment, key, decrypt=False))
    rebuilt = bytes(header) + bytes(payload) + key
    # Parse the result here so malformed size/checksum edits fail before output.
    metadata, _ = parse_container(rebuilt)
    if not all(metadata["segment_checksums_valid"].values()):
        raise ValueError("rebuilt UPF failed its own segment checksum validation")
    return rebuilt


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("--out", type=pathlib.Path)
    parser.add_argument("--replace-usb-main", type=pathlib.Path)
    parser.add_argument("--replace-ble", type=pathlib.Path)
    parser.add_argument("--replace-slave", type=pathlib.Path)
    parser.add_argument("--repack", type=pathlib.Path)
    args = parser.parse_args()
    data = args.path.read_bytes()
    metadata, segments = parse_container(data)
    if not all(metadata["segment_checksums_valid"].values()):
        raise ValueError("UPF has an invalid decrypted segment checksum")
    metadata["decrypted_sha256"] = {
        name: hashlib.sha256(segment).hexdigest().upper() for name, segment in segments
    }
    print(json.dumps(metadata, indent=2))
    if args.out:
        args.out.mkdir(parents=True, exist_ok=True)
        (args.out / "header.bin").write_bytes(data[:HEADER_SIZE])
        (args.out / "metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        for name, segment in segments:
            (args.out / f"{name}.bin").write_bytes(segment)
    if args.repack:
        replacements = {
            name: path.read_bytes()
            for name, path in {
                "usb_main": args.replace_usb_main,
                "ble": args.replace_ble,
                "slave": args.replace_slave,
            }.items()
            if path is not None
        }
        rebuilt = pack_container(data, replacements)
        args.repack.parent.mkdir(parents=True, exist_ok=True)
        args.repack.write_bytes(rebuilt)
        print(json.dumps({
            "repacked_path": str(args.repack),
            "repacked_size": len(rebuilt),
            "repacked_sha256": hashlib.sha256(rebuilt).hexdigest().upper(),
            "byte_identical_to_input": rebuilt == data,
        }, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
