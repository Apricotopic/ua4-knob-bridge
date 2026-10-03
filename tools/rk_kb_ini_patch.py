#!/usr/bin/env python3
"""Build the explicit dual-bank M75 KB.ini companion (offline only)."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path


EXPECTED_SHA256 = "C8332CB635EB30D4CE517446A5C0F20BD5B8504607E55B377A5FE2DA3424F978"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if text.count(old) != 1:
        raise SystemExit(f"refusing {label}: expected one source occurrence")
    return text.replace(old, new, 1)


def section(text: str, name: str) -> str:
    match = re.search(rf"(?ms)^\[{re.escape(name)}\]\r?\n(.*?)(?=^\[|\Z)", text)
    if not match:
        raise SystemExit(f"missing [{name}] section")
    return match.group(1).rstrip("\r\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()

    source = args.source.read_bytes()
    if digest(source) != EXPECTED_SHA256:
        raise SystemExit(f"refusing source SHA-256 {digest(source)}")
    newline = "\r\n" if b"\r\n" in source else "\n"
    text = source.decode("utf-8")
    text = replace_once(text, "Psd=3,0,0,0,0,A7", "Psd=3,0,0,0,0,A7" + newline + "DoublePro=1",
                        "dual-bank option")

    key = section(text, "KEY")
    fn1 = section(text, "FN1")
    key = replace_once(key, ";PgDn" + newline + "K81=656,148,678,170, 0x02,0x22,0x00,93",
        ";PgDn physical key: normal layer now End" + newline +
        "K81=656,148,678,170, 0x02,0x23,0x00,93" + newline +
        ";Hidden encoder counter-clockwise: reserved Consumer usage 0x02FE" + newline +
        "K82=0,0,0,0, 0x09,0x01,0x020002FE,123" + newline +
        ";Hidden encoder clockwise: reserved Consumer usage 0x02FF" + newline +
        "K83=0,0,0,0, 0x09,0x01,0x020002FF,125", "Windows KEY changes")
    fn1 = replace_once(fn1, ";PgDn" + newline + "K81=0x02,0x23,0x00",
        ";PgDn physical key: Fn layer now Page Down" + newline + "K81=0x02,0x22,0x00", "Windows FN1 change")

    text = re.sub(r"(?ms)^\[KEY\]\r?\n.*?(?=^\[FN1\])", "[KEY]" + newline + key + newline + newline, text, count=1)
    text = re.sub(r"(?ms)^\[FN1\]\r?\n.*\Z", "[FN1]" + newline + fn1 + newline, text, count=1)

    mac_key = key
    mac_key = replace_once(mac_key, "K72=107,225,137,247, 0x02,0x5B,0x00,11",
                           "K72=107,225,137,247, 0x02,0xA4,0x00,11", "macOS Option")
    mac_key = replace_once(mac_key, "K73=155,225,185,247, 0x02,0xA4,0x00,17",
                           "K73=155,225,185,247, 0x02,0x5B,0x00,17", "macOS Command")
    mac_key = replace_once(mac_key, "K12=513,22,535,44, 0x02,0x7A,0x00,72",
                           "K12=513,22,535,44, 0x09,0x01,0x020000EA,72", "macOS F11")
    mac_key = replace_once(mac_key, "K13=552,22,574,44, 0x02,0x7B,0x00,78",
                           "K13=552,22,574,44, 0x09,0x01,0x020000E9,78", "macOS F12")
    mac_fn1 = fn1
    mac_fn1 = replace_once(mac_fn1, "K12=0x09,0x01,0x020000ea", "K12=0x02,0x7A,0x00", "macOS Fn+F11")
    mac_fn1 = replace_once(mac_fn1, "K13=0x09,0x01,0x020000e9", "K13=0x02,0x7B,0x00", "macOS Fn+F12")
    text += newline + "[KEY_MAC]" + newline + mac_key + newline + newline
    text += "[FN1_MAC]" + newline + mac_fn1 + newline + newline
    text += "[FN2_MAC]" + newline + newline + "[TAP_MAC]" + newline

    candidate = text.encode("utf-8")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(candidate)
    if args.output.read_bytes() != candidate:
        raise SystemExit("post-write candidate-file verification failed")
    manifest = {
        "status": "OFFLINE CANDIDATE - DO NOT INSTALL WITHOUT EXPLICIT APPROVAL",
        "format": "RK official control-software dual-bank device definition",
        "source": str(args.source.resolve()), "output": str(args.output.resolve()),
        "source_size": len(source), "output_size": len(candidate),
        "source_sha256": digest(source), "output_sha256": digest(candidate),
        "line_endings_preserved": "CRLF" if newline == "\r\n" else "LF",
        "changes": ["DoublePro=1", "complete KEY_MAC and FN1_MAC sections", "empty FN2_MAC and TAP_MAC sections",
                    "both banks: PgDn/End swap and encoder 0x02FE/0x02FF",
                    "macOS: left Option/Command order and direct F11/F12 volume"],
        "upf_firmware_modified": False,
    }
    manifest_path = args.manifest or args.output.with_suffix(args.output.suffix + ".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
