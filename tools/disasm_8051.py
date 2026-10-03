#!/usr/bin/env python3
"""Linear 8051 disassembly of a selected ROM range using disasm51's opcode table."""

from __future__ import annotations

import argparse
from pathlib import Path

from disasm51.codeanalyzer import InstrArgsAnalyzer
from disasm51.instructions import ArgType, Instructions


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=Path)
    parser.add_argument("start", type=lambda value: int(value, 0))
    parser.add_argument("end", type=lambda value: int(value, 0))
    args = parser.parse_args()
    rom = args.path.read_bytes()
    table = Instructions()
    pc = args.start
    while pc < min(args.end, len(rom)):
        opcode = rom[pc]
        instruction = table.instructions.get(opcode)
        if instruction is None or pc + instruction.length > len(rom):
            print(f"{pc:04X}: {opcode:02X}       db 0x{opcode:02X}")
            pc += 1
            continue
        values: list[str] = []
        if instruction.args:
            decoder = InstrArgsAnalyzer(rom, pc, pc + instruction.length, 0)
            for kind in instruction.args:
                decoded = decoder.next_arg(kind)
                if decoded is None:
                    values.append("?")
                    continue
                actual_kind, value, _ = decoded
                prefix = "#" if actual_kind == ArgType.IMM else ""
                values.append(f"{prefix}0x{value:02X}" if value <= 0xFF else f"{prefix}0x{value:04X}")
        text = instruction.mnemonic.format(*values) if values else instruction.mnemonic
        raw = rom[pc : pc + instruction.length].hex(" ").upper()
        print(f"{pc:04X}: {raw:<8} {text}")
        pc += instruction.length
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
