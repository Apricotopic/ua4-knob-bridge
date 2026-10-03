#!/usr/bin/env python3
"""Locate immediate-byte references to a virtual address in a 32-bit PE."""

from __future__ import annotations

import argparse
import pathlib
import struct

import capstone
import lief


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("address", type=lambda value: int(value, 0))
    parser.add_argument("--before", type=lambda value: int(value, 0), default=0x80)
    parser.add_argument("--after", type=lambda value: int(value, 0), default=0x60)
    args = parser.parse_args()
    binary = lief.parse(str(args.path))
    if not isinstance(binary, lief.PE.Binary):
        raise TypeError("not a PE binary")
    section = binary.get_section(".text")
    if section is None:
        raise ValueError(".text missing")
    base = binary.optional_header.imagebase + section.virtual_address
    code = bytes(section.content)
    needle = struct.pack("<I", args.address)
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    decoder.skipdata = True
    offset = 0
    count = 0
    while True:
        offset = code.find(needle, offset)
        if offset < 0:
            break
        count += 1
        reference = base + offset
        print(f"\nXREF[{count}] immediate-bytes va=0x{reference:X}")
        start = max(0, offset - args.before)
        end = min(len(code), offset + args.after)
        for instruction in decoder.disasm(code[start:end], base + start):
            marker = "=>" if instruction.address <= reference < instruction.address + instruction.size else "  "
            print(f"{marker} {instruction.address:08X}: {instruction.mnemonic:9} {instruction.op_str}")
        offset += 1
    print(f"\nTOTAL {count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
