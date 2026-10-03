#!/usr/bin/env python3
"""Locate string references in a 32-bit PE and disassemble their neighborhoods."""

from __future__ import annotations

import argparse
import pathlib
import struct

import capstone
import lief


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("text")
    parser.add_argument("--before", type=lambda value: int(value, 0), default=0x100)
    parser.add_argument("--after", type=lambda value: int(value, 0), default=0x80)
    args = parser.parse_args()

    binary = lief.parse(str(args.path))
    if not isinstance(binary, lief.PE.Binary):
        raise TypeError("not a PE binary")
    image_base = binary.optional_header.imagebase
    raw = args.path.read_bytes()
    needles = [args.text.encode("ascii") + b"\0", args.text.encode("utf-16le") + b"\0\0"]
    text_section = binary.get_section(".text")
    if text_section is None:
        raise ValueError(".text missing")
    text_bytes = bytes(text_section.content)
    text_va = image_base + text_section.virtual_address
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    decoder.skipdata = True

    found = False
    for needle in needles:
        offset = 0
        while True:
            offset = raw.find(needle, offset)
            if offset < 0:
                break
            found = True
            owner = next(
                (section for section in binary.sections
                 if section.offset <= offset < section.offset + section.size),
                None,
            )
            if owner is None:
                offset += 1
                continue
            rva = owner.virtual_address + offset - owner.offset
            string_va = image_base + rva
            print(f"\nSTRING file=0x{offset:X} rva=0x{rva:X} va=0x{string_va:X} encoding={'utf16' if needle[1:2] == b'\\0' else 'ascii'}")
            encoded = struct.pack("<I", string_va)
            reference_offset = 0
            while True:
                reference_offset = text_bytes.find(encoded, reference_offset)
                if reference_offset < 0:
                    break
                reference_va = text_va + reference_offset
                print(f"XREF immediate-bytes va=0x{reference_va:X}")
                start_offset = max(0, reference_offset - args.before)
                end_offset = min(len(text_bytes), reference_offset + args.after)
                start_va = text_va + start_offset
                for instruction in decoder.disasm(text_bytes[start_offset:end_offset], start_va):
                    marker = "=>" if instruction.address <= reference_va < instruction.address + instruction.size else "  "
                    print(f"{marker} {instruction.address:08X}: {instruction.mnemonic:8} {instruction.op_str}")
                reference_offset += 1
            offset += 1
    if not found:
        print("string not found")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
