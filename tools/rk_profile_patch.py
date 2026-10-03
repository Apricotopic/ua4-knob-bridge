#!/usr/bin/env python3
"""Build and verify the reversible RK M75 dual-bank profile candidate.

The tool is offline-only: it opens no HID device. It requires the exact backed-
up pro.dct, constructs the missing macOS bank, patches both banks, and emits a
complete byte/record manifest.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


EXPECTED_SIZE = 24_344
EXPECTED_SHA256 = "9F868673D3CC725306294B8D8103B93F229D385D2950EE3249D5336836874A73"
RECORD_SIZE = 16
RECORDS_PER_LAYER = 132
PROFILE_BASE = 0x3B
LAYER_STRIDE = RECORDS_PER_LAYER * RECORD_SIZE
BANK_STRIDE = 4 * LAYER_STRIDE


def record_offset(bank: int, layer: int, key_number: int) -> int:
    if bank not in (0, 1) or layer not in (0, 1, 2, 3):
        raise ValueError("bank must be 0..1 and layer 0..3")
    if not 1 <= key_number <= RECORDS_PER_LAYER:
        raise ValueError("key number must be in 1..132")
    return PROFILE_BASE + bank * BANK_STRIDE + layer * LAYER_STRIDE + (key_number - 1) * RECORD_SIZE


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def action_record(scan_index: int, action: int) -> bytes:
    return bytes([0x09, scan_index, scan_index, scan_index, 0, 0, 0, 0, 0]) + action.to_bytes(4, "little") + bytes([0x01, 0, 0])


def fn_key_record(virtual_key: int) -> bytes:
    return bytes([0x02, 0, 0, 0, 0, 0, virtual_key, 0, 0, 0, 0, 0, 0, 0, 0, 0])


def patch_record(candidate: bytearray, changes: list[dict[str, object]], *, bank: int, layer: int,
                 key: int, after: bytes, name: str, expected_before: bytes | None = None) -> None:
    offset = record_offset(bank, layer, key)
    before = bytes(candidate[offset:offset + RECORD_SIZE])
    if expected_before is not None and before != expected_before:
        raise SystemExit(f"refusing unexpected record at 0x{offset:04X}: {before.hex(' ').upper()}")
    if len(after) != RECORD_SIZE:
        raise SystemExit(f"invalid replacement length for {name}")
    candidate[offset:offset + RECORD_SIZE] = after
    changes.append({
        "name": name,
        "bank": "Windows" if bank == 0 else "macOS",
        "layer": ["KEY", "FN1", "FN2", "TAP"][layer],
        "key": f"K{key}",
        "record_offset": f"0x{offset:04X}",
        "before": before.hex(" ").upper(),
        "after": after.hex(" ").upper(),
    })


def byte_differences(before: bytes, after: bytes) -> list[dict[str, int | str]]:
    return [{"offset": f"0x{i:04X}", "before": a, "after": b}
            for i, (a, b) in enumerate(zip(before, after, strict=True)) if a != b]


def changed_records(before: bytes, after: bytes, bank: int) -> list[dict[str, object]]:
    result = []
    for layer in range(4):
        for key in range(1, RECORDS_PER_LAYER + 1):
            offset = record_offset(bank, layer, key)
            old = before[offset:offset + RECORD_SIZE]
            new = after[offset:offset + RECORD_SIZE]
            if old != new:
                result.append({
                    "layer": ["KEY", "FN1", "FN2", "TAP"][layer],
                    "key": f"K{key}",
                    "offset": f"0x{offset:04X}",
                    "before": old.hex(" ").upper(),
                    "after": new.hex(" ").upper(),
                })
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--rollback-output", type=Path)
    parser.add_argument("--rollback-manifest", type=Path)
    args = parser.parse_args()

    source = args.source.read_bytes()
    source_hash = sha256(source)
    if len(source) != EXPECTED_SIZE or source_hash != EXPECTED_SHA256:
        raise SystemExit(f"refusing source size/hash {len(source)}/{source_hash}")

    mac_start = PROFILE_BASE + BANK_STRIDE
    mac_end = mac_start + BANK_STRIDE
    if any(source[mac_start:mac_end]):
        raise SystemExit("refusing: backed-up macOS bank is not entirely zero")

    candidate = bytearray(source)
    # Whole tables are transferred. Start from the complete M75 key geometry
    # and defaults, then apply explicit Mac deltas below rather than blindly
    # leaving the Windows semantics in the second bank.
    candidate[mac_start:mac_end] = source[PROFILE_BASE:PROFILE_BASE + BANK_STRIDE]
    semantic_changes: list[dict[str, object]] = []

    pgdn = bytes.fromhex("02 5D 5D 5D 00 00 22 00 00 00 00 00 00 00 00 00")
    end = bytes.fromhex("02 5D 5D 5D 00 00 23 00 00 00 00 00 00 00 00 00")
    fn_end = bytes.fromhex("02 00 00 00 00 00 23 00 00 00 00 00 00 00 00 00")
    fn_pgdn = bytes.fromhex("02 00 00 00 00 00 22 00 00 00 00 00 00 00 00 00")

    for bank in (0, 1):
        patch_record(candidate, semantic_changes, bank=bank, layer=0, key=81, after=end,
                     expected_before=pgdn, name="PgDn -> End")
        patch_record(candidate, semantic_changes, bank=bank, layer=1, key=81, after=fn_pgdn,
                     expected_before=fn_end, name="Fn+PgDn -> Page Down")
        patch_record(candidate, semantic_changes, bank=bank, layer=0, key=82,
                     after=action_record(123, 0x020002FE), expected_before=bytes(RECORD_SIZE),
                     name="Encoder CCW -> reserved Consumer 0x02FE")
        patch_record(candidate, semantic_changes, bank=bank, layer=0, key=83,
                     after=action_record(125, 0x020002FF), expected_before=bytes(RECORD_SIZE),
                     name="Encoder CW -> reserved Consumer 0x02FF")

    # macOS left-side ordering: Option then Command. Right Alt remains Option.
    win_k72 = bytes(source[record_offset(0, 0, 72):record_offset(0, 0, 72) + RECORD_SIZE])
    win_k73 = bytes(source[record_offset(0, 0, 73):record_offset(0, 0, 73) + RECORD_SIZE])
    # The converter represents modifiers with fields at bytes 5 and 8. Keep
    # each key's scan tuple (bytes 1..3) and transplant the complete action
    # payload (bytes 4..15) from the other modifier record.
    mac_k72 = win_k72[:4] + win_k73[4:]
    mac_k73 = win_k73[:4] + win_k72[4:]
    patch_record(candidate, semantic_changes, bank=1, layer=0, key=72, after=mac_k72,
                 expected_before=win_k72, name="macOS left Win-position -> Left Option")
    patch_record(candidate, semantic_changes, bank=1, layer=0, key=73, after=mac_k73,
                 expected_before=win_k73, name="macOS left Alt-position -> Left Command")

    # M75 documentation specifies direct F11/F12 volume in Mac mode. Fn restores
    # standard F11/F12. F1..F10 retain the M75 definitions unchanged.
    patch_record(candidate, semantic_changes, bank=1, layer=0, key=12,
                 after=action_record(72, 0x020000EA), name="macOS F11 -> Volume Down")
    patch_record(candidate, semantic_changes, bank=1, layer=0, key=13,
                 after=action_record(78, 0x020000E9), name="macOS F12 -> Volume Up")
    patch_record(candidate, semantic_changes, bank=1, layer=1, key=12,
                 after=fn_key_record(0x7A), name="macOS Fn+F11 -> F11")
    patch_record(candidate, semantic_changes, bank=1, layer=1, key=13,
                 after=fn_key_record(0x7B), name="macOS Fn+F12 -> F12")

    result = bytes(candidate)

    # Dual-bank rollback: keep the explicit factory-equivalent Mac semantics,
    # but remove all requested custom records from both banks. This can be sent
    # through the same profile path to undo the controlled validation write.
    rollback = bytearray(result)
    for bank in (0, 1):
        rollback[record_offset(bank, 0, 81):record_offset(bank, 0, 81) + RECORD_SIZE] = pgdn
        rollback[record_offset(bank, 1, 81):record_offset(bank, 1, 81) + RECORD_SIZE] = fn_end
        rollback[record_offset(bank, 0, 82):record_offset(bank, 0, 82) + RECORD_SIZE] = bytes(RECORD_SIZE)
        rollback[record_offset(bank, 0, 83):record_offset(bank, 0, 83) + RECORD_SIZE] = bytes(RECORD_SIZE)
    rollback_result = bytes(rollback)

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(result)
    rollback_path = args.rollback_output or args.output.with_name("pro.rollback.dct")
    rollback_path.parent.mkdir(parents=True, exist_ok=True)
    rollback_path.write_bytes(rollback_result)
    diffs = byte_differences(source, result)
    manifest = {
        "status": "OFFLINE CANDIDATE - DO NOT APPLY WITHOUT EXPLICIT APPROVAL",
        "format": "RK official software pro.dct dual-bank onboard configuration",
        "source": str(args.source.resolve()), "output": str(args.output.resolve()),
        "source_size": len(source), "output_size": len(result),
        "source_sha256": source_hash, "output_sha256": sha256(result),
        "bank_layout": {"record_size": 16, "records_per_layer": 132, "layer_stride": "0x0840",
                        "bank_stride": "0x2100", "windows_base": "0x003B", "macos_base": "0x213B"},
        "macos_construction": "complete Windows/default tables copied, then explicit Mac modifier/F11/F12 deltas and required cross-mode mappings applied",
        "dual_bank_rollback": str(rollback_path.resolve()),
        "dual_bank_rollback_sha256": sha256(rollback_result),
        "rollback_to_candidate_changed_byte_count": len(byte_differences(rollback_result, result)),
        "rollback_to_candidate_byte_differences": byte_differences(rollback_result, result),
        "semantic_changes": semantic_changes,
        "windows_bank_record_diff": changed_records(source, result, 0),
        "macos_bank_record_diff": changed_records(source, result, 1),
        "changed_byte_count": len(diffs), "byte_differences": diffs,
        "upf_firmware_modified": False, "hid_device_opened": False,
    }
    manifest_path = args.manifest or args.output.with_suffix(args.output.suffix + ".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    rollback_manifest = {
        "status": "OFFLINE DUAL-BANK ROLLBACK - DO NOT APPLY UNLESS ROLLING BACK AN APPROVED WRITE",
        "format": "RK official software pro.dct dual-bank onboard configuration",
        "source": str(args.source.resolve()), "output": str(rollback_path.resolve()),
        "source_sha256": source_hash, "output_sha256": sha256(rollback_result),
        "purpose": "restore original PgDn/Fn+PgDn and clear custom encoder slots in both banks while retaining factory-equivalent macOS Option/Command and F11/F12 behavior",
        "candidate_sha256": sha256(result),
        "candidate_to_rollback_changed_byte_count": len(byte_differences(result, rollback_result)),
        "candidate_to_rollback_byte_differences": byte_differences(result, rollback_result),
        "upf_firmware_modified": False,
    }
    rollback_manifest_path = args.rollback_manifest or rollback_path.with_suffix(rollback_path.suffix + ".manifest.json")
    rollback_manifest_path.write_text(json.dumps(rollback_manifest, indent=2) + "\n", encoding="utf-8")

    written = args.output.read_bytes()
    if written != result or len(written) != EXPECTED_SIZE or rollback_path.read_bytes() != rollback_result:
        raise SystemExit("post-write candidate-file verification failed")
    if written[:PROFILE_BASE] != source[:PROFILE_BASE] or written[mac_end:] != source[mac_end:]:
        raise SystemExit("change escaped the two action-bank region")
    print(json.dumps({k: manifest[k] for k in ("source_sha256", "output_sha256", "changed_byte_count")}, indent=2))
    print(f"Windows changed records: {len(manifest['windows_bank_record_diff'])}")
    print(f"macOS changed records: {len(manifest['macos_bank_record_diff'])}")
    print(f"Rollback SHA-256: {sha256(rollback_result)}")
    print(f"Rollback -> candidate changed bytes: {len(byte_differences(rollback_result, result))}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
