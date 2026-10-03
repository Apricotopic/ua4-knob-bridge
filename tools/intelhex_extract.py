#!/usr/bin/env python3
"""Materialize an Intel HEX address space as a sparse-filled binary for analysis."""

from __future__ import annotations

import argparse
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    memory: dict[int, int] = {}
    base = 0
    for line_number, raw in enumerate(args.input.read_text(encoding="ascii").splitlines(), 1):
        if not raw.startswith(":"):
            continue
        record = bytes.fromhex(raw[1:])
        count, address, kind = record[0], int.from_bytes(record[1:3], "big"), record[3]
        if (sum(record) & 0xFF) != 0:
            raise ValueError(f"checksum failure at line {line_number}")
        data = record[4 : 4 + count]
        if kind == 0:
            memory.update((base + address + i, value) for i, value in enumerate(data))
        elif kind == 2:
            base = int.from_bytes(data, "big") << 4
        elif kind == 4:
            base = int.from_bytes(data, "big") << 16
        elif kind == 1:
            break
    if not memory:
        raise ValueError("no data records")
    start, end = min(memory), max(memory) + 1
    image = bytearray([0xFF]) * end
    for address, value in memory.items():
        image[address] = value
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(image)
    print(f"records={len(memory)} address=0x{start:X}..0x{end - 1:X} output={len(image)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
