#!/usr/bin/env python3
"""List direct x86 PE calls/jumps to an explicit virtual address."""

from __future__ import annotations

import argparse
import pathlib

import capstone
import lief


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("target", type=lambda value: int(value, 0))
    parser.add_argument("--before", type=lambda value: int(value, 0), default=0x80)
    parser.add_argument("--after", type=lambda value: int(value, 0), default=0x40)
    args = parser.parse_args()

    binary = lief.parse(str(args.path))
    if not isinstance(binary, lief.PE.Binary):
        raise TypeError("not a PE binary")
    section = binary.get_section(".text")
    if section is None:
        raise ValueError(".text missing")
    image_base = binary.optional_header.imagebase
    code = bytes(section.content)
    code_va = image_base + section.virtual_address
    decoder = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    decoder.detail = True
    decoder.skipdata = True
    instructions = list(decoder.disasm(code, code_va))
    callers: list[int] = []
    for instruction in instructions:
        if instruction.mnemonic not in {"call", "jmp"} or not instruction.operands:
            continue
        operand = instruction.operands[0]
        if operand.type == capstone.x86.X86_OP_IMM and operand.imm == args.target:
            callers.append(instruction.address)

    print(f"TARGET=0x{args.target:X} CALLERS={len(callers)}")
    for index, caller in enumerate(callers, 1):
        print(f"\nCALLER[{index}] va=0x{caller:X}")
        start = max(0, caller - code_va - args.before)
        end = min(len(code), caller - code_va + args.after)
        for instruction in decoder.disasm(code[start:end], code_va + start):
            marker = "=>" if instruction.address == caller else "  "
            print(f"{marker} {instruction.address:08X}: {instruction.mnemonic:8} {instruction.op_str}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
