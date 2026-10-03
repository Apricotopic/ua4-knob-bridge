#!/usr/bin/env python3
"""Create the minimal RK M75 BLE/main-controller firmware patch.

The script is deliberately fail-closed: it accepts only the exact BLE segment
from the verified V20.08.2.11 package and checks every preimage before editing.
It never modifies its input file.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


EXPECTED_INPUT_SHA256 = (
    "F7BA5A20E66CA369A850B991D6CBEEABDD30447B7C395D649E60AB2C328A1EE2"
)
EXPECTED_SIZE = 0xF000


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def action(value: int) -> bytes:
    return value.to_bytes(4, "big")


def build_patch_set() -> list[dict[str, object]]:
    patches: list[dict[str, object]] = []

    # Four normal/base maps: PgDn (HID 0x4E) becomes End (HID 0x4D).
    for table in (0xBC00, 0xBE00, 0xC000, 0xC200):
        offset = table + 93 * 4
        patches.append(
            {
                "offset": offset,
                "before": action(0x0000004E),
                "after": action(0x0000004D),
                "reason": f"base table 0x{table:04X}: PgDn scan 93 -> End",
            }
        )

    # Four Fn maps: End (HID 0x4D) becomes PgDn (HID 0x4E).
    for table in (0xC400, 0xC600, 0xC800, 0xCA00):
        offset = table + 93 * 4
        patches.append(
            {
                "offset": offset,
                "before": action(0x0000004D),
                "after": action(0x0000004E),
                "reason": f"Fn table 0x{table:04X}: PgDn scan 93 End -> PgDn",
            }
        )

    # Encoder handler at 8051 code address/file offset 0x844A.
    # CCW: Consumer 0x00EA -> 0x02FE.  The replacement writes high byte 0x02
    # to XRAM 0x0F56 and returns instead of updating the Windows-volume mirror.
    patches.extend(
        [
            {
                "offset": 0x848F,
                "before": bytes.fromhex("EA"),
                "after": bytes.fromhex("FE"),
                "reason": "encoder CCW low usage byte: Volume Down -> 0xFE",
            },
            {
                "offset": 0x8491,
                "before": bytes.fromhex("90 0F 61 E0 FF F4 60 2D"),
                "after": bytes.fromhex("90 0F 56 74 02 F0 22 00"),
                "reason": "encoder CCW: write usage high byte 0x02, return",
            },
            {
                "offset": 0x84B0,
                "before": bytes.fromhex("E9"),
                "after": bytes.fromhex("FF"),
                "reason": "encoder CW low usage byte: Volume Up -> 0xFF",
            },
            {
                "offset": 0x84B2,
                "before": bytes.fromhex("90 0F 61 E0 F4 60 0D"),
                "after": bytes.fromhex("90 0F 56 74 02 F0 22"),
                "reason": "encoder CW: write usage high byte 0x02, return",
            },
        ]
    )
    return patches


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()

    original = args.input.read_bytes()
    original_hash = sha256(original)
    if len(original) != EXPECTED_SIZE:
        raise SystemExit(
            f"refusing: expected {EXPECTED_SIZE} bytes, got {len(original)}"
        )
    if original_hash != EXPECTED_INPUT_SHA256:
        raise SystemExit(
            "refusing: input is not the verified official BLE segment; "
            f"SHA-256={original_hash}"
        )

    candidate = bytearray(original)
    manifest_patches: list[dict[str, object]] = []
    occupied: set[int] = set()
    for patch in build_patch_set():
        offset = int(patch["offset"])
        before = bytes(patch["before"])
        after = bytes(patch["after"])
        if len(before) != len(after):
            raise AssertionError("patch changes segment size")
        found = bytes(candidate[offset : offset + len(before)])
        if found != before:
            raise SystemExit(
                f"refusing: preimage mismatch at 0x{offset:04X}: "
                f"expected {before.hex().upper()}, got {found.hex().upper()}"
            )
        touched = set(range(offset, offset + len(before)))
        if occupied & touched:
            raise AssertionError(f"overlapping patch at 0x{offset:04X}")
        occupied |= touched
        candidate[offset : offset + len(after)] = after
        manifest_patches.append(
            {
                "offset": f"0x{offset:04X}",
                "length": len(before),
                "before": before.hex(" ").upper(),
                "after": after.hex(" ").upper(),
                "reason": patch["reason"],
            }
        )

    changed_offsets = [
        index for index, (a, b) in enumerate(zip(original, candidate)) if a != b
    ]
    candidate_bytes = bytes(candidate)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(candidate_bytes)

    manifest = {
        "format": "rk-m75-v20.08.2.11-ble-patch-v1",
        "input": str(args.input.resolve()),
        "output": str(args.output.resolve()),
        "input_size": len(original),
        "input_sha256": original_hash,
        "output_size": len(candidate_bytes),
        "output_sha256": sha256(candidate_bytes),
        "changed_byte_count": len(changed_offsets),
        "changed_offsets": [f"0x{offset:04X}" for offset in changed_offsets],
        "patches": manifest_patches,
    }
    if args.manifest:
        args.manifest.parent.mkdir(parents=True, exist_ok=True)
        args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
