#!/usr/bin/env python3
"""Disassemble an explicit virtual-address range from an x86 PE file."""

from __future__ import annotations

import argparse
import pathlib

import capstone
import lief


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("start", type=lambda value: int(value, 0))
    parser.add_argument("end", type=lambda value: int(value, 0))
    args = parser.parse_args()
    binary = lief.parse(str(args.path))
    if not isinstance(binary, lief.PE.Binary):
        raise TypeError("not a PE binary")
    image_base = binary.optional_header.imagebase
    start_rva = args.start - image_base
    end_rva = args.end - image_base
    section = next(
        (candidate for candidate in binary.sections
         if candidate.virtual_address <= start_rva < candidate.virtual_address + candidate.virtual_size),
        None,
    )
    if section is None or end_rva > section.virtual_address + section.virtual_size:
        raise ValueError("requested range is not contained in one section")
    start_offset = start_rva - section.virtual_address
    end_offset = end_rva - section.virtual_address
    code = bytes(section.content)[start_offset:end_offset]
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    decoder.skipdata = True
    for instruction in decoder.disasm(code, args.start):
        print(f"{instruction.address:08X}: {instruction.mnemonic:9} {instruction.op_str}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
