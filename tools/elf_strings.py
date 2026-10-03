#!/usr/bin/env python3
"""Print offset-qualified ASCII and UTF-16LE strings from a binary."""

from __future__ import annotations

import argparse
import pathlib
import re


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("--minimum", type=int, default=4)
    args = parser.parse_args()
    data = args.path.read_bytes()

    ascii_pattern = re.compile(rb"[\x20-\x7e]{%d,}" % args.minimum)
    utf16_pattern = re.compile(rb"(?:[\x20-\x7e]\x00){%d,}" % args.minimum)
    found: list[tuple[int, str, str]] = []
    for match in ascii_pattern.finditer(data):
        found.append((match.start(), "A", match.group().decode("ascii", "replace")))
    for match in utf16_pattern.finditer(data):
        found.append((match.start(), "W", match.group().decode("utf-16le", "replace")))
    for offset, encoding, text in sorted(found):
        print(f"{offset:08x} {encoding} {text}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
