#!/usr/bin/env python3
"""Disassemble an ELF virtual-address range and annotate known symbols/PLT slots."""

from __future__ import annotations

import argparse
import pathlib

import capstone
import lief


def c_string(binary: lief.ELF.Binary, address: int) -> str:
    data = bytes(binary.get_content_from_virtual_address(address, 512))
    return data.split(b"\0", 1)[0].decode("utf-8", "replace")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("path", type=pathlib.Path)
    parser.add_argument("start", type=lambda value: int(value, 0))
    parser.add_argument("end", type=lambda value: int(value, 0))
    args = parser.parse_args()

    binary = lief.parse(str(args.path))
    if not isinstance(binary, lief.ELF.Binary):
        raise TypeError("not an ELF binary")

    labels: dict[int, str] = {}
    for function in list(binary.exported_functions):
        if function.address:
            labels[function.address] = function.name

    plt = binary.get_section(".plt")
    if plt:
        plt_relocations = [rel for rel in binary.pltgot_relocations]
        reserved = plt.size - 16 * len(plt_relocations)
        for index, relocation in enumerate(plt_relocations):
            labels[plt.virtual_address + reserved + index * 16] = relocation.symbol.name + "@plt"

    # The native registration table is an array of JNINativeMethod triples.
    relative = {rel.address: rel.addend for rel in binary.relocations if str(rel.type).endswith("AARCH64_RELATIVE")}
    for table_address in sorted(relative):
        if table_address + 16 not in relative:
            continue
        name_address = relative[table_address]
        signature_address = relative.get(table_address + 8)
        function_address = relative.get(table_address + 16)
        if not signature_address or not function_address:
            continue
        try:
            name = c_string(binary, name_address)
            signature = c_string(binary, signature_address)
        except Exception:
            continue
        if name and signature.startswith("("):
            labels[function_address] = f"JNI {name}{signature}"

    code = bytes(binary.get_content_from_virtual_address(args.start, args.end - args.start))
    decoder = capstone.Cs(capstone.CS_ARCH_ARM64, capstone.CS_MODE_LITTLE_ENDIAN)
    for instruction in decoder.disasm(code, args.start):
        if instruction.address in labels:
            print(f"\n{labels[instruction.address]}:")
        annotation = ""
        if instruction.mnemonic in {"bl", "b"}:
            try:
                target = int(instruction.op_str.lstrip("#"), 16)
                if target in labels:
                    annotation = f"  ; {labels[target]}"
            except ValueError:
                pass
        print(f"{instruction.address:08x}: {instruction.mnemonic:8} {instruction.op_str}{annotation}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
