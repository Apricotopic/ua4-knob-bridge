#!/usr/bin/env python3
"""Disassemble direct 32-bit PE references to a named import's IAT slot."""

from __future__ import annotations

import argparse
import pathlib
import struct

import capstone
import lief


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("import_name")
    parser.add_argument("--before", type=lambda value: int(value, 0), default=0x100)
    parser.add_argument("--after", type=lambda value: int(value, 0), default=0x80)
    args = parser.parse_args()
    binary = lief.parse(str(args.path))
    if not isinstance(binary, lief.PE.Binary):
        raise TypeError("not a PE binary")
    entry = next(
        (entry for library in binary.imports for entry in library.entries
         if entry.name == args.import_name),
        None,
    )
    if entry is None:
        raise ValueError(f"import not found: {args.import_name}")
    section = binary.get_section(".text")
    if section is None:
        raise ValueError(".text missing")
    image_base = binary.optional_header.imagebase
    iat_va = image_base + entry.iat_address
    code = bytes(section.content)
    code_va = image_base + section.virtual_address
    encoded = struct.pack("<I", iat_va)
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    decoder.skipdata = True
    print(f"IMPORT {args.import_name} IAT_VA=0x{iat_va:X}")
    offset = 0
    count = 0
    thunks: set[int] = set()
    while True:
        offset = code.find(encoded, offset)
        if offset < 0:
            break
        count += 1
        reference_va = code_va + offset
        # MSVC's local import thunks are six-byte `jmp dword ptr [IAT]`
        # instructions; the four-byte IAT address begins two bytes in.
        if offset >= 2 and code[offset - 2:offset] == b"\xFF\x25":
            thunks.add(reference_va - 2)
        print(f"\nXREF[{count}] immediate-bytes va=0x{reference_va:X}")
        start = max(0, offset - args.before)
        end = min(len(code), offset + args.after)
        for instruction in decoder.disasm(code[start:end], code_va + start):
            marker = "=>" if instruction.address <= reference_va < instruction.address + instruction.size else "  "
            print(f"{marker} {instruction.address:08X}: {instruction.mnemonic:8} {instruction.op_str}")
        offset += 1
    if thunks:
        instructions = list(decoder.disasm(code, code_va))
        for thunk in sorted(thunks):
            callers = []
            target_text = f"0x{thunk:x}"
            for instruction in instructions:
                if instruction.mnemonic in {"call", "jmp"} and instruction.op_str.lower() == target_text:
                    callers.append(instruction.address)
            print(f"\nTHUNK 0x{thunk:X} CALLERS={len(callers)}")
            for caller_index, caller in enumerate(callers, 1):
                print(f"\nCALLER[{caller_index}] va=0x{caller:X}")
                caller_offset = caller - code_va
                start = max(0, caller_offset - args.before)
                end = min(len(code), caller_offset + args.after)
                for instruction in decoder.disasm(code[start:end], code_va + start):
                    marker = "=>" if instruction.address == caller else "  "
                    print(f"{marker} {instruction.address:08X}: {instruction.mnemonic:8} {instruction.op_str}")
    print(f"\nTOTAL {count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
