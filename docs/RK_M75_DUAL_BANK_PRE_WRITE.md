# RK M75 dual-bank pre-write approval package

Date: 2026-08-21  
Target: `Gaming KB`, `VID_258A&PID_0163`, firmware display `20.08`  
Status: **offline candidate ready; physical keyboard not written; approval required**

## Decision

Use the official persistent onboard action-profile mechanism. Do not patch or
launch the protected UPF updater. The Windows bridge is OS-mode agnostic and
accepts only Consumer usages `0x02FE`/`0x02FF` from the M75 Consumer collection.

No installed RK file, live `%LOCALAPPDATA%` profile, keyboard configuration,
firmware, driver, registry value, service, or UA4 persistent state was changed.
The only physical-device operations during investigation were non-persistent
read requests; the live profile still has the original hash.

## Evidence and confidence

### Confirmed

- RK interface: `MI_01/Col02`, Usage Page `0x0C`, Consumer Control, Report ID 2,
  16-bit array range `0x0000..0x02FF`.
- `Fn+A` and `Fn+S` are firmware-local actions `0x07000020` (`Windows_Mode`) and
  `0x07000021` (`Mac_Mode`); their absence from Raw Input is expected.
- The official application supports two banks: `KEY/FN1/FN2/TAP` and
  `KEY_MAC/FN1_MAC/FN2_MAC/TAP_MAC`. Bank stride is `0x2100`; table stride is
  `0x0840`; each table has 132 records of 16 bytes.
- The M75 definition omits `DoublePro` and every `_MAC` section. The saved
  profile consequently has a populated Windows region and a zero macOS region.
  This explains why earlier customization stopped applying after `Fn+S`.
- Mode switching did not re-enumerate or change the RK USB collections in the
  marked trace. The Windows bridge does not detect or query active mode.
- Other official RK rotary definitions consistently use scan 123 for CCW and
  125 for CW. The M75 exposes unused K82/K83 slots at exactly those positions.
- `0x02FE`/`0x02FF` are reserved Consumer usages inside the M75 descriptor's
  report range. Windows has no native volume action for them.
- UA4 control is standard KS/UAC2 Feature Unit volume through
  `IOCTL_KS_PROPERTY`: VID/PID `20B1/3026`, volume node 3, KS property 4,
  channels 0/1, range `-16256..0` in steps of 128. It changes the UA4 display
  while Windows master remains unchanged.
- UA4 logical `0..100` mapping round-trips all 101 values. The connected UA4
  readback smoke test passed at volume 100.

### Strongly inferred; controlled validation gate

- This M75 uses scan 123/125 for its physical encoder. Cross-model evidence is
  consistent, but the first approved write must confirm directions live.
- A detent emits one active/release cycle. The bridge collapses repeated active
  reports until neutral; separate duplicate cycles must be measured in the
  detent-count test rather than hidden by speculative time debounce.
- Mac left-side order is Option/Command (physical Win-position -> Left Alt;
  physical Alt-position -> Left GUI). The binary records use the official
  converter's full modifier bitfields, not a guessed one-byte usage change.

### Unresolved until approved hardware validation

- Actual `0x02FE`/`0x02FF` reports from this physical M75.
- Exact one-detent count, rapid reversal behavior, and long-run missed/duplicate
  rate.
- Persistence after mode switching, reconnect, and power cycle.
- Factory-equivalent Mac modifier/F-row behavior on every host. The first test
  explicitly checks these and rolls back on any mismatch.

## Bank construction

The macOS bank is not a blind Windows clone. The official transfer writes whole
tables, so a sparse bank could disable unspecified keys. The candidate starts
with the complete known M75 action/scan geometry, then applies explicit Mac
deltas:

- K72: Left Win-position -> Left Option;
- K73: Left Alt-position -> Left Command;
- K75 remains Right Option;
- F11/F12 directly emit Volume Down/Up in Mac mode, matching the provided M75
  documentation; Fn+F11/F12 emit standard F11/F12;
- all other F-row and key actions remain as defined by the supplied M75 table;
- required PgDn and encoder records are then applied equally to both banks.

The companion `KB.ini` enables `DoublePro=1`, contains complete `KEY_MAC` and
`FN1_MAC` sections, and explicit empty `FN2_MAC`/`TAP_MAC` sections.

## Exact semantic records

| Bank | Layer/key | Offset | Final 16-byte record | Meaning |
|---|---|---:|---|---|
| Windows | KEY K81 | `0x053B` | `02 5D 5D 5D 00 00 23 00 00 00 00 00 00 00 00 00` | PgDn -> End |
| Windows | FN1 K81 | `0x0D7B` | `02 00 00 00 00 00 22 00 00 00 00 00 00 00 00 00` | Fn+PgDn -> Page Down |
| Windows | KEY K82 | `0x054B` | `09 7B 7B 7B 00 00 00 00 00 FE 02 00 02 01 00 00` | CCW scan 123 -> `0x02FE` |
| Windows | KEY K83 | `0x055B` | `09 7D 7D 7D 00 00 00 00 00 FF 02 00 02 01 00 00` | CW scan 125 -> `0x02FF` |
| macOS | KEY K81 | `0x263B` | same as Windows K81 | PgDn -> End |
| macOS | FN1 K81 | `0x2E7B` | same as Windows FN1 K81 | Fn+PgDn -> Page Down |
| macOS | KEY K82 | `0x264B` | same as Windows K82 | CCW -> `0x02FE` |
| macOS | KEY K83 | `0x265B` | same as Windows K83 | CW -> `0x02FF` |

Knob press is untouched. No mute action exists in the profile or bridge.

Mac-specific records are K12 `0x21EB` (direct Volume Down), K13 `0x21FB`
(direct Volume Up), FN1 K12 `0x2A2B` (F11), FN1 K13 `0x2A3B` (F12), K72
`0x25AB` (Option), and K73 `0x25BB` (Command). All complete records appear in
the manifest.

## Hashes and binary diff

| Artifact | Size | SHA-256 |
|---|---:|---|
| Original `pro.dct` | 24,344 | `9F868673D3CC725306294B8D8103B93F229D385D2950EE3249D5336836874A73` |
| Dual-bank candidate `pro.dct` | 24,344 | `50BCB07447B5B41425C5A8D2104D508FAA44A464E6BA3C0E081D698F9319FE2F` |
| Dual-bank rollback `pro.rollback.dct` | 24,344 | `7BACB8109EA3EE8E1CE0256828C47EF8500DF5161DC2FACC904FBC967B1FD844` |
| Original M75 `KB.ini` | 5,552 | `C8332CB635EB30D4CE517446A5C0F20BD5B8504607E55B377A5FE2DA3424F978` |
| Candidate dual-bank `KB.ini` | 10,655 | `5F702B2765898F3BEDD34F49EA3F5268CC2EE4218FF96C222F69613F7AC45792` |

- Original -> candidate: 537 changed byte positions. Windows: 4 changed
  records. macOS: 123 records change because the original disk region is zero
  and the safe candidate fully populates KEY (83) and FN1 (40).
- Dual-bank rollback -> candidate: exactly 36 byte positions, confined to the
  eight requested K81/K82/K83 records.
- Bytes before `0x003B` and after the two action-bank region are identical.
- The complete 537-byte list and all 127 changed records are in
  `pro.dct.manifest.json`; nothing is summarized away.
- No `.upf` exists in the candidate and the original firmware hash remains
  `64E3ABD6DB2433570AFBF633C08329D26E8DDBE9526893B139DB5BF13479CC8B`.

## Exact first-write scope

After approval, use wired mode and the official profile/action transfer only.
The logical scope is the two onboard action banks (four 132x16 tables per bank,
`0x4200` bytes total before transport framing). Because the protocol transfers
whole selected tables, the physical transaction is not a four-record write.

Out of scope and prohibited: firmware/UPF, bootloader, descriptors, lighting
effects, debounce parameters, wireless settings, driver, registry, UA4
firmware/persistence, or Windows master volume.

The live AppData `pro.dct` still has the original SHA-256 above and the official
RK process is not running. Re-hash both sources immediately before applying;
abort on any drift.

## Recovery and rollback

1. Stop the bridge and RK software.
2. Send `pro.rollback.dct` through the same wired dual-bank action-profile path.
   It clears both custom encoder slots and restores PageDown/End in both banks,
   while retaining the explicit factory-equivalent Mac modifier/F11/F12 table.
3. Restore the original installed `KB.ini` and live AppData `pro.dct` from the
   hashed backup.
4. Reconnect and verify original Windows and Mac behavior.
5. If the normal profile transfer cannot recover the table, RK's official
   fallback is factory reset through its USB software or (for most RGB models)
   `Fn+Spacebar`; this clears user configuration and therefore is a second-tier
   recovery step, not part of the first write.
6. Do not launch the UPF updater. Firmware recovery remains a last resort and
   requires separate approval.

The rollback image has been regenerated twice from the hash-locked source,
re-read byte-for-byte, and compared against the candidate. Physical rollback is
necessarily untested until a write is authorized; it is the first action on any
failed encoder or Mac regression.

## Approval-time validation

Immediately after the first write, with the bridge initially disabled:

1. One CCW detent: exactly one active `02 FE 02` plus neutral; no `EA`.
2. One CW detent: exactly one active `02 FF 02` plus neutral; no `E9`.
3. PgDn -> End; Fn+PgDn -> Page Down.
4. `Fn+A`: repeat all four checks.
5. `Fn+S`: repeat all four checks; also check Option/Command and F11/F12.
6. `Fn+A`: repeat all four checks again.
7. On any wrong scan/direction/action, send rollback immediately.
8. Enable the bridge and verify each detent changes UA4 display by exactly one,
   clamps at 0/100, and leaves Windows master at 100.
9. Repeat after USB reconnect and keyboard power cycle.

Regression gate: several hundred mixed detents, rapid direction changes,
repeated mode switching, application restart, and UA4 disconnect/reconnect;
zero duplicates, misses, native volume events, or unrelated-key regressions.

## Risks

| Risk | Mitigation / rollback trigger |
|---|---|
| 123/125 reversed or ignored | One-detent test before bridge; immediate rollback |
| Reserved usage rewritten/blocked | Raw capture before UA4 command; rollback |
| More than one active/release cycle per detent | Count test; characterize before adding any bounded debounce |
| Full macOS table differs from factory runtime | Test modifiers/F-row immediately; rollback |
| Transfer interruption | Wired stable USB; resend rollback; factory reset second tier |
| Brick | Configuration path never enters bootloader; UPF not present or launched |

## Files

- Candidate and manifests: `analysis/patches/rk_m75_dual_bank_candidate/`
- Hash-locked original: `analysis/backups/rk_m75/pre_custom_encoder/rk_appdata_2026-08-21/pro.dct`
- Builders: `tools/rk_profile_patch.py`, `tools/rk_kb_ini_patch.py`
- Marked trace assessment: `analysis/RK_OS_MODE_TRACE_ANALYSIS.md`
- Bridge source: `src/Ua4KnobBridge/`

This document is the approval boundary. No onboard write occurs until the user
explicitly approves it.
