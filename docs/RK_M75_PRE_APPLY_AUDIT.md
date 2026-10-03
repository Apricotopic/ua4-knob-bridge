# RK M75 pre-apply firmware and configuration audit

Date: 2026-08-21  
Target: `Gaming KB`, USB `VID_258A&PID_0163`, `bcdDevice 20.08`  
Status: **superseded first-bank candidate; do not apply; keyboard not written;
firmware not flashed**

> Cross-mode audit update: the official software contains a second macOS action
> bank at profile offset `+0x2100`. The candidate described below changes only
> the Windows/default bank and is therefore intentionally withdrawn. See
> `analysis/RK_OS_MODE_BANK_EVIDENCE.md`. A replacement will be generated only
> after the read-only `Fn+A -> Fn+S -> Fn+A` report trace is complete.

## Decision and approval boundary

The requested behavior can be represented by the RK official software's persistent onboard key/action profile. The supplied UPF firmware does **not** need to be modified or flashed.

The UPF's outer container transform was completely reproduced and round-tripped byte-for-byte. Its three decoded payloads remain protected/encoded at another layer, however. They do not expose a trustworthy instruction image, key matrix routine, encoder state machine, debounce routine, or HID report builder. Making instruction-level changes inside those payloads would therefore be unverifiable and is rejected.

The provisional candidate changes four records in a copy of `pro.dct` and adds
the two missing encoder definitions to a copy of `Dev/0163/KB.ini`. It is kept
only as audit history and must not be applied because it omits the newly
confirmed macOS bank. No candidate has been copied into RK's installed
directory or `%LOCALAPPDATA%`, and no HID configuration-write command has been
sent.

## Findings by confidence

### Confirmed

- The connected keyboard is `USB\VID_258A&PID_0163`, product `Gaming KB`, manufacturer `SINO WEALTH`, `bcdDevice 20.08`.
- `MI_00` is a boot keyboard. `MI_01/Col02` is HID Consumer Control, Usage Page `0x0C`, Usage `0x01`, input Report ID `2`, three-byte input report, with a 16-bit array range `0x0000..0x02FF`.
- The official device definition declares `LayoutKeyNum=84` but ends the base `[KEY]` table at `K81`. Records `K82`, `K83`, and `K84` are absent. The saved profile contains zero records at those positions.
- The normal `PgDn` record is layer 0/K81 at file offset `0x053B`; usage byte `0x22` is Page Down. The Fn record is layer 1/K81 at `0x0D7B`; usage byte `0x23` is End.
- The official software uses `HidD_SetFeature`/`HidD_GetFeature` on the RK vendor collection. Its profile action converter processes 132 records per layer. Action type `0x09` accepts and byte-swaps the stored 32-bit action value without a Consumer-usage whitelist.
- Other official RK device definitions encode encoder decrement as scan index `123`, action `0x020000EA`, and encoder increment as scan index `125`, action `0x020000E9`. This pairing appears consistently across the inspected configurations that expose rotary volume actions.
- USB HID Usage Tables 1.7 reserves Consumer usages `0x02D5..0x04FF`; therefore `0x02FE` and `0x02FF` are unassigned. They are also inside this keyboard descriptor's declared maximum `0x02FF`.
- The original UPF is for VID/PID `258A/0163`, carries versions `V2.11`, `V20.08`, and `Y1.04`, and identifies chip signature `87F88050`. Its outer per-block transform and inverse reproduce every original ciphertext byte.
- The UPF and update executable are unchanged and backed up with hashes. No updater was launched.

### Strongly inferred; requires the first controlled hardware validation

- The M75 firmware uses the same hidden scan indices `123/125` for counter-clockwise/clockwise as the other RK rotary products. Evidence is the cross-device convention, M75's declared-but-missing record capacity, the zero K82/K83 profile slots, and the generic official action converter.
- Once these slots contain `0x020002FE`/`0x020002FF`, the firmware will emit Report ID 2 followed by usages `0x02FE`/`0x02FF`. The descriptor and converter allow them; the candidate has not been written, so the emitted live reports cannot yet be confirmed.
- Windows will not bind a built-in system action to these reserved usages. Raw Input should still deliver the report to the bridge.

### Unresolved without an approved configuration write or unobtainable plaintext firmware

- Whether one physical detent yields exactly one active/release report pair on this particular keyboard.
- Whether the M75 build applies its saved table to the encoder or hard-codes `E9/EA` despite the shared scan-index convention.
- Instruction-level encoder debounce, key-matrix scanning, layer-state implementation, and USB report-construction code.
- A physical bootloader recovery test. Performing it would itself require the firmware flash the user prohibited before approval.

## Audit of reported defects and optimizations

| Area | Evidence | Decision |
|---|---|---|
| M75 encoder remapping | `LayoutKeyNum=84`, only K1..K81 defined; K82/K83 profile slots are zero; other RK rotary configs define scan 123/125 | Fix the missing device-definition records and populate only K82/K83 in the onboard profile candidate. |
| PgDn/Fn layer | Exact current records and observed requested behavior agree | Swap only HID keyboard usages `0x22` and `0x23` in K81 across the two layers. |
| Knob press | No requested function and no verified M75 profile slot | Leave K84 zero/untouched. No mute action is added. |
| Debounce | `KB.ini` exposes `DebounceUI=1,2` and `DebounceHW=1,3`, but no physical bounce measurement or readable firmware routine exists | Do not change firmware debounce. Normalize active-to-neutral reports in the bridge and validate detent count on hardware. |
| Matrix/layers beyond K81 | No concrete defect established | No change. |
| USB/HID descriptors | Existing Consumer report already supports 16-bit usages through `0x02FF` | No descriptor or firmware change. |
| Firmware code, clocks, BLE, RGB, power | Inner UPF payload is opaque and no concrete defect can be tied to a function | No speculative optimization. |
| Updater | Static parser accepts this header/family/VID/PID/chip tuple; original package is hashed | Do not modify or run it for this configuration-only candidate. |

No additional “bug fix” was selected. That is deliberate: the evidence does not support any other low-risk binary change.

## Candidate changes

### Onboard `pro.dct`

Source:

`analysis/backups/rk_m75/pre_custom_encoder/rk_appdata_2026-08-21/pro.dct`

Candidate:

`analysis/patches/rk_m75_candidate/pro.dct`

| Record | Offset | Original | Candidate | Meaning |
|---|---:|---|---|---|
| layer 0/K81 | `0x053B` | `02 5D 5D 5D 00 00 22 00 00 00 00 00 00 00 00 00` | `02 5D 5D 5D 00 00 23 00 00 00 00 00 00 00 00 00` | Normal PgDn becomes End. |
| layer 1/K81 | `0x0D7B` | `02 00 00 00 00 00 23 00 00 00 00 00 00 00 00 00` | `02 00 00 00 00 00 22 00 00 00 00 00 00 00 00 00` | Fn+PgDn becomes Page Down. |
| layer 0/K82 | `0x054B` | 16 zero bytes | `09 7B 7B 7B 00 00 00 00 00 FE 02 00 02 01 00 00` | Scan 123, type 09, action `0x020002FE`. |
| layer 0/K83 | `0x055B` | 16 zero bytes | `09 7D 7D 7D 00 00 00 00 00 FF 02 00 02 01 00 00` | Scan 125, type 09, action `0x020002FF`. |

The candidate remains 24,344 bytes. Exactly 18 byte positions differ. The complete byte list is in `analysis/patches/rk_m75_candidate/pro.dct.manifest.json`.

| File | SHA-256 |
|---|---|
| original `pro.dct` | `9F868673D3CC725306294B8D8103B93F229D385D2950EE3249D5336836874A73` |
| candidate `pro.dct` | `7665A3469C301D08D53D942E7CBFBC06765BECB0109222B4D45A75BEC2B357FF` |

### Official software `KB.ini` companion

The companion definition makes the hidden M75 encoder records visible to the official 132-record converter. Its CRLF convention is preserved.

```diff
-;PgDn
-K81=656,148,678,170, 0x02,0x22,0x00,93
+;PgDn physical key: normal layer now End
+K81=656,148,678,170, 0x02,0x23,0x00,93
+;Hidden encoder counter-clockwise: reserved Consumer usage 0x02FE
+K82=0,0,0,0, 0x09,0x01,0x020002FE,123
+;Hidden encoder clockwise: reserved Consumer usage 0x02FF
+K83=0,0,0,0, 0x09,0x01,0x020002FF,125
...
-;PgDn
-K81=0x02,0x23,0x00
+;PgDn physical key: Fn layer now Page Down
+K81=0x02,0x22,0x00
```

| File | SHA-256 |
|---|---|
| original `Dev/0163/KB.ini` | `C8332CB635EB30D4CE517446A5C0F20BD5B8504607E55B377A5FE2DA3424F978` |
| candidate `KB.ini` | `6BB44583A1091996094E62F84C1C0ADDD275FFB7B2E02BAE09E8207B14C0FF37` |

### UPF firmware binary

No bytes changed. The candidate contains no UPF.

| Original recovery artifact | SHA-256 |
|---|---|
| `KB_RK872_v258A_p0163_F90(2008)_M75_8805(2.11)_V20.08.2.11.upf` | `64E3ABD6DB2433570AFBF633C08329D26E8DDBE9526893B139DB5BF13479CC8B` |
| `PH_UpdateTool_V1.30_20240913.exe` | `8C34FE5E3C27EE2684787D6E95A13736DC82B496B3787193B2E647C11533301C` |

## Validation completed offline

1. Original package and active `%LOCALAPPDATA%\RKKB\0163` files were copied before edits and SHA-256 hashed.
2. Candidate builders refuse any source with the wrong size/hash or unexpected record bytes.
3. `pro.dct` was re-read after generation; all four complete records were checked.
4. A full-file comparison confirms 18 changed byte positions and no size change.
5. `KB.ini` is generated from the exact original hash, requires unique source blocks, preserves CRLF, and has a reviewed textual diff.
6. The outer UPF decoder/encoder round-trips every supplied encrypted segment exactly; no candidate UPF was generated.
7. `%LOCALAPPDATA%\RKKB\0163\pro.dct` still has the original hash. The official RK process is not running.

## Controlled first-application test (requires approval)

The first write should use wired USB mode and should be treated as a configuration test, not a firmware flash.

1. Re-hash the live installed `KB.ini` and `%LOCALAPPDATA%\RKKB\0163\pro.dct`; abort on drift and make a new backup if needed.
2. Keep the original profile and official software available locally.
3. Place the candidate companion/profile, launch the official RK software, and send the profile once through its normal onboard configuration path.
4. Before running the UA4 bridge, rotate one detent each way while logging RK `MI_01/Col02` Raw Input. Required reports are active `02 FE 02` then neutral, and active `02 FF 02` then neutral (byte order as delivered by Report ID 2). Windows master must remain unchanged.
5. Test PgDn = End and Fn+PgDn = Page Down.
6. Run a 20-detent count in each direction. The bridge event count must be exactly 20. If not, do not compensate with acceleration; characterize duplicate timing and apply a bounded report de-bounce.
7. Only after the input test passes, enable the UA4 bridge and confirm 20 detents produce exactly 20 displayed UA4 levels, clamped at 0/100.

Any continued `E9/EA`, missing custom report, wrong direction, wrong detent count, or unintended key change is a rollback condition.

## Rollback and recovery

### Configuration rollback

1. Close the RK software and bridge.
2. Restore the original `Dev/0163/KB.ini` (hash above).
3. Restore the original `pro.dct` from `analysis/backups/rk_m75/pre_custom_encoder/rk_appdata_2026-08-21/pro.dct` to `%LOCALAPPDATA%\RKKB\0163\pro.dct`.
4. Open the official RK software and resend that original profile through the same onboard configuration path.
5. Reconnect the keyboard and verify normal PgDn/Fn+PgDn and original encoder behavior.

Restoring disk files alone does not revert the keyboard's already-written onboard table; the original profile must be resent.

### Firmware recovery

The configuration candidate does not enter the bootloader or replace firmware, so firmware recovery should not be necessary. The untouched official UPF/updater pair is preserved under `analysis/backups/rk_m75/original_firmware_package_2026-08-21` as a last-resort recovery source.

Static inspection confirms that the updater recognizes the supplied package identity. A real recovery flash is **not** claimed as verified, because proving it would require the prohibited pre-approval flash. For that reason, an UPF flash is not part of this plan.

## Risk assessment

| Risk | Likelihood before live validation | Impact | Mitigation |
|---|---|---|---|
| M75 ignores K82/K83 and keeps hard-coded volume usages | Medium | Windows volume still changes | Raw-log with bridge disabled; immediately resend original profile. No global suppression. |
| Scan direction 123/125 is reversed on M75 | Low/medium | Direction reversed | One-detent preflight; swap only K82/K83 actions if observed. |
| Official app rewrites an unknown reserved usage | Low/medium | Custom event absent or altered | Capture live report before enabling bridge; static converter has no whitelist. |
| Encoder bounce produces multiple active/release pairs | Unknown | More than one UA4 level per detent | 20-detent count; bounded de-bounce only if measured. |
| Keymap slot/index assumption affects another control | Low | Unexpected key action | Test all changed controls; rollback profile. |
| Configuration write interruption | Low | Onboard table may be incomplete | Wired mode, stable USB, original profile ready; power-cycle and resend original. |
| Firmware brick | Very low for this plan | High | No firmware/bootloader/UPF write occurs. |

## Workspace files created or modified

- Created `tools/rk_profile_patch.py`.
- Created `tools/rk_kb_ini_patch.py`.
- Created candidate `analysis/patches/rk_m75_candidate/pro.dct` and its manifest.
- Created candidate `analysis/patches/rk_m75_candidate/KB.ini` and its manifest.
- Created this audit document.
- Previously created read-only UPF analysis tool `tools/rk_upf.py` and decoded analysis copies.

No installed RK file, `%LOCALAPPDATA%` file, registry value, driver, service, startup setting, or physical-device state was modified.
