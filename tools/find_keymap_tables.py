#!/usr/bin/env python3
"""Find compiled RK M75 keymaps by scoring known scan-index/HID pairs."""

from __future__ import annotations

import argparse
import pathlib


# Scan indices are from the official M75 Dev/0163/KB.ini. Values are USB HID
# Keyboard/Keypad usages for the factory Windows layer.
EXPECTED = {
    0: 0x29, 12: 0x3A, 18: 0x3B, 24: 0x3C, 30: 0x3D, 36: 0x3E,
    42: 0x3F, 48: 0x40, 54: 0x41, 60: 0x42, 66: 0x43, 72: 0x44,
    78: 0x45, 84: 0x4C,
    1: 0x35, 7: 0x1E, 13: 0x1F, 19: 0x20, 25: 0x21, 31: 0x22,
    37: 0x23, 43: 0x24, 49: 0x25, 55: 0x26, 61: 0x27, 67: 0x2D,
    73: 0x2E, 79: 0x2A, 91: 0x4A,
    2: 0x2B, 8: 0x14, 14: 0x1A, 20: 0x08, 26: 0x15, 32: 0x17,
    38: 0x1C, 44: 0x18, 50: 0x0C, 56: 0x12, 62: 0x13, 68: 0x2F,
    74: 0x30, 80: 0x31, 92: 0x4B,
    3: 0x39, 9: 0x04, 15: 0x16, 21: 0x07, 27: 0x09, 33: 0x0A,
    39: 0x0B, 45: 0x0D, 51: 0x0E, 57: 0x0F, 63: 0x33, 69: 0x34,
    81: 0x28, 93: 0x4E,
    4: 0xE1, 10: 0x1D, 16: 0x1B, 22: 0x06, 28: 0x19, 34: 0x05,
    40: 0x11, 46: 0x10, 52: 0x36, 58: 0x37, 64: 0x38, 82: 0xE5,
    88: 0x52,
    5: 0xE0, 11: 0xE3, 17: 0xE2, 35: 0x2C, 53: 0xE6, 65: 0xE4,
    59: 0x00, 83: 0x50, 89: 0x51, 95: 0x4F,
}


def score(data: bytes, base: int, stride: int, field: int) -> int:
    return sum(data[base + scan * stride + field] == usage for scan, usage in EXPECTED.items())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("--top", type=int, default=20)
    args = parser.parse_args()
    data = args.path.read_bytes()
    for stride in (1, 2, 4, 8):
        for field in range(stride):
            extent = max(EXPECTED) * stride + field + 1
            candidates = []
            for base in range(len(data) - extent + 1):
                value = score(data, base, stride, field)
                if value:
                    candidates.append((value, base))
            candidates.sort(reverse=True)
            print(f"stride={stride} field={field}")
            for value, base in candidates[:args.top]:
                print(f"  score={value:2}/{len(EXPECTED)} file=0x{base:05X} va=0x{base + 0x1000:05X}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
