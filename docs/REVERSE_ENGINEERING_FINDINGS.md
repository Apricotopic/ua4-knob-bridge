# Reverse-engineering findings

Date: 2026-08-21  
Target host: Windows, with the user's connected RK M75 and Shanling UA4  
Confidence labels: **CONFIRMED**, **STRONGLY INFERRED**, and **UNRESOLVED**

## Executive result

The safe runtime path is:

```text
RK M75 onboard dual-bank action profile
  -> reserved Consumer usage 0x02FE / 0x02FF
  -> Windows Raw Input, filtered to VID_258A/PID_0163/MI_01/Col02
  -> user-space translation
  -> Windows Kernel Streaming topology property
  -> existing CTUAC2 driver
  -> UAC2 Feature Unit 3 VOLUME_CONTROL
  -> UA4 MCU
  -> ES9069Q registers 74/75 (per-channel digital attenuation)
```

This is hierarchy option 1 from the request: a standard Windows/UAC path. No UA4 HID output, WinUSB binding, official proprietary DLL, or private vendor protocol is required on Windows. The application never uses `IAudioEndpointVolume` and does not write Windows master volume.

## RK M75

### Identity and interfaces

| Finding | Status | Evidence |
|---|---|---|
| USB identity is `VID 258A`, `PID 0163`, `bcdDevice 2008`; product string is `Gaming KB`; HID maker string is `SINO WEALTH`. | **CONFIRMED** | Live Windows enumeration in `tools/DeviceProbe`; the official software maps `0163` to `RK-M75RGB New layout` in `RK_M75_Keyboard/RK Keyboard Software/Cfg.ini:159`. |
| `MI_00` is the boot-keyboard interface. | **CONFIRMED** | Live HID/PNP enumeration. |
| `MI_01` contains eight top-level HID collections. The knob collection is `Col02`, Usage Page `0x0C` (Consumer), Usage `0x01` (Consumer Control), input report length 3. | **CONFIRMED** | Live HID preparsed-data enumeration. Other collections cover System Control, keyboard, mouse, and vendor-defined functions. |
| The knob report uses Report ID `0x02` followed by a 16-bit Consumer usage, with logical range `0x0000..0x02FF`. | **CONFIRMED** | Live HID button-cap structure for `MI_01/Col02`. |

### Factory and candidate encoder events

The unmodified factory behavior and final candidate are:

| Physical action | Factory usage | Candidate report | Status |
|---|---:|---|---|
| Clockwise | `0x00E9` Volume Increment | `02 FF 02`, then `02 00 00` | scan 125 **STRONGLY INFERRED**, live validation pending |
| Counter-clockwise | `0x00EA` Volume Decrement | `02 FE 02`, then `02 00 00` | scan 123 **STRONGLY INFERRED**, live validation pending |
| Press | unchanged | ignored by bridge | deliberately unused |

The factory mapping is supported by the Consumer descriptor and M75 F-row media definitions. Encoder scan 123/125 is consistent across the inspected official RK rotary definitions and fits the M75's missing K82/K83 records. The candidate stores actions `0x020002FE`/`0x020002FF` in both banks. Physical output remains explicitly validation-gated rather than mislabeled as captured.

### Remapping and interception

- **CONFIRMED:** Windows sees rotation as Consumer Control, not a vendor-HID volume protocol.
- **CONFIRMED:** Raw Input can receive it in the background and retain the originating HID device path. `RawInputKeyboardKnob.cs` registers Usage Page `0x0C`, Usage `0x01`, then accepts only the exact RK path.
- **CONFIRMED:** `DeviceDriver.exe` supports separate `KEY/FN1/FN2/TAP` and `_MAC` banks, bank stride `0x2100`, and independent transport selectors.
- **CONFIRMED:** the supplied M75 definition omits `DoublePro` and `_MAC` sections; its saved macOS profile region is entirely zero. This is the concrete cause of customization disappearing after `Fn+S`.
- **CONFIRMED:** the official action converter accepts type `0x09` 32-bit actions without a Consumer whitelist, and `0x02FE/0x02FF` fit the descriptor maximum.
- **CONFIRMED:** a device-specific Raw Input listener can accept the custom reports without suppressing volume keys from any other device.
- **CONFIRMED:** the marked trace shows no USB re-enumeration at `Fn+A/Fn+S`. The mode commands are consumed onboard and the bridge does not detect them.

Recommended method: persistent dual-bank onboard actions plus exact-device Raw Input. There is no low-level hook, global suppression, kernel filter, or UPF patch. Other keyboards/headsets keep their normal Windows volume behavior.

No physical RK firmware/configuration has been written. The audited candidate is offline under `analysis/patches/rk_m75_dual_bank_candidate` and awaits explicit approval.

## Shanling UA4

### Identity and USB layout

| Item | Result | Status |
|---|---|---|
| USB identity | `VID 20B1`, `PID 3026`, `bcdDevice 0201`, serial/product `Shanling_UA4` | **CONFIRMED** |
| Interface 0 | AudioControl, USB Audio Class 2 (`class 1`, `subclass 1`, `protocol 0x20`) | **CONFIRMED** |
| Interface 1 | AudioStreaming; alternate settings 1..4; audio OUT endpoint `0x01`, feedback IN endpoint `0x81` | **CONFIRMED** |
| Interface 2 | HID with interrupt IN `0x82` and OUT endpoint `0x03`; its exposed top-level collections are input-only System/Consumer controls | **CONFIRMED** |
| Windows driver | Existing Shanling `CTUAC2` 6.0.1.4031; its INF explicitly matches `USB\VID_20B1&PID_3026&MI_00` | **CONFIRMED** |

The live 343-byte configuration descriptor contains this AudioControl chain:

```text
Clock Source ID 5
USB Streaming Input Terminal ID 1, two channels
Feature Unit ID 3, source ID 1
  master: mute + volume, read/write
  channel 1: volume, read/write
  channel 2: volume, read/write
Output Terminal ID 4, source ID 3
```

The raw Feature Unit descriptor, at configuration offset `0x003C`, is:

```text
12 24 06 03 01 0F 00 00 00 0C 00 00 00 0C 00 00 00 00
```

### Exact UAC2 protocol

Eddict Player's ARM64 `libusbaudio.so` dynamically registers `com/shanling/usbExclusive/UsbAudioDecode`. Relevant native methods include `uac2SetVolume(II)I` at virtual address `0x6544` and `getFeatureUnitId` at `0x699C`.

`uac2SetVolume` performs the following operation:

1. Accepts UI volume only in `0..100`.
2. Uses the UAC volume minimum, maximum, and resolution queried from the device.
3. Distributes 101 UI values monotonically over the raw UAC range.
4. Sends the resulting little-endian signed 16-bit value through `libusb_control_transfer` to each playback channel.

The SET request is:

```text
bmRequestType = 0x21  // host-to-device, class, interface
bRequest      = 0x01  // SET_CUR
wValue        = 0x0200 | channel  // VOLUME_CONTROL selector 2
wIndex        = (FeatureUnitId << 8) | AudioControlInterface
wLength       = 2
data          = signed raw volume, little endian
timeout       = 0
```

For UA4 this resolves to Feature Unit `3`, interface `0`, and channels `1` and `2`: `wIndex=0x0300`, `wValue=0x0201` then `0x0202`. A nonnegative `libusb_control_transfer` result is treated as success; there is no command-specific response payload. The normal USB control status stage and the transfer return code are the acknowledgement. A negative transfer aborts with `-1`; successful channel writes return `1`.

Mute is the standard Feature Unit MUTE_CONTROL selector `1`, exposed as a writable Windows KS mute node. The application uses the corresponding KS property instead of issuing libusb requests against a driver-owned device.

### Range, step, and readback

The live Windows KS topology has four nodes; node `2` is mute and node `3` is volume on this host. The final application discovers only the volume node by GUID and contains no mute path.

`KSPROPERTY_AUDIO_VOLUMELEVEL` reports two channels with:

```text
KS minimum = -16256
KS maximum =      0
KS step    =    128
```

The driver represents one raw UAC unit as 128 KS units, so the effective signed raw range is `-127..0`, resolution `1`. The Eddict mapping is exactly:

```text
UI 0..27   -> raw -127 + (2 * UI)
UI 28..100 -> raw UI - 100
```

Examples: `0 -> -127`, `1 -> -125`, `27 -> -73`, `28 -> -72`, `75 -> -25`, `100 -> 0`. One application step is one Eddict/UA4 UI level; at the low end some UI steps span two raw units so all 101 positions cover the device's 128 raw positions.

Current volume is readable with a KS GET for `KSPROPSETID_Audio / KSPROPERTY_AUDIO_VOLUMELEVEL` on both channels. Writes are read back and reflected in the GUI.

### Windows transport selected by the application

The live KS interface is under `KSCATEGORY_AUDIO` and contains `vid_20b1&pid_3026&mi_00`. The program opens that interface with `CreateFile` and uses:

```text
IOCTL_KS_PROPERTY = 0x002F0003
Property set      = KSPROPSETID_Audio
Volume property   = KSPROPERTY_AUDIO_VOLUMELEVEL (4)
Flags             = GET or SET, plus KSPROPERTY_TYPE_TOPOLOGY
Node              = discovered volume node
Channel           = 0 and 1
Volume data       = raw UAC value * 128, signed 32-bit KS value
```

This uses the already-bound CTUAC2 driver correctly and ultimately produces the standard UAC2 Feature Unit request above. It does not detach or replace the driver.

### Dynamic validation

- **CONFIRMED:** live read returned UI volume `100`, raw/KS `0/0`, mute false.
- **CONFIRMED:** a reversible one-step test wrote both channels from KS `0` to `-128`, read the new value back, then restored both channels to `0` and verified the restoration.
- **CONFIRMED:** during the direct KS hardware-volume write, the Windows Core Audio endpoint master remained scalar `1.0` / `0 dB` / 100%.
- **CONFIRMED (historical diagnostic only):** a low-level hook could block an injected Windows Volume Down key, but that architecture was rejected and no hook remains in the final source.
- **CONFIRMED:** after testing, UA4 volume was restored to UI `100`, mute false, and Windows master was restored/verified at 100%.

No updater was launched and no UA4 firmware, flash, EEPROM, filesystem, calibration, persistent configuration, driver, or service was modified.

## What UA4 independent volume changes internally

### Confirmed conclusion: ES9069Q DAC-chip digital attenuation

The continuous volume is implemented by the ES9069Q DAC's per-channel digital volume registers, not by Windows software attenuation and not primarily by an analog PGA or RT6863 amplifier gain setting.

Evidence chain:

1. **CONFIRMED:** Shanling specifies an ES9069Q DAC and dual RT6863 headphone amplifiers for UA4.
2. **CONFIRMED:** extracted UA4 firmware contains the strings `PCM_CH1`, `PCM_CH2`, `DSD_CH1`, `DSD_CH2`, `DAC Volume`, and `System Volume`.
3. **CONFIRMED:** firmware ROM `0x6615..0x6679` contains a 101-byte, monotonically decreasing volume table. ROM `0x667A` contains the UI maximum `100`. Selected table values are `UI 0 -> FF`, `1 -> F0`, `2 -> DC`, `3 -> C8`, `4 -> B4`, `5 -> A0`, `31 -> 4A`, and `100 -> 00`.
4. **CONFIRMED:** the 8051 routine around ROM `0x52D8` indexes that table for the two channels and reaches a register-write routine at `0x7BA1` with starting register `0x4A` (decimal 74), writing the two channel bytes consecutively.
5. **CONFIRMED:** the ES9069Q data sheet defines register 74 as `VOLUME_CH1` and register 75 as `VOLUME_CH2`. Values `0..255` implement `0..-127.5 dB` in `0.5 dB` increments, and the data sheet classifies this signal-path volume as digital.

The firmware table is nonlinear with respect to the UA4's displayed `0..100` number; that table is the device's UX taper into the DAC attenuation register. The precise MCU-to-DAC physical control bus is **STRONGLY INFERRED** to be I2C or SPI from the register-write pattern and ES9069Q control options, but the exact selected bus/pins were not established. This does not affect the safe UAC runtime protocol.

The RT6863 devices are downstream headphone amplifiers. UA4 may separately expose high/low amplifier gain as a discrete mode, but the continuous 0..100 volume path above is the ES9069Q's digital attenuation.

## APK and Windows-software investigation notes

- `EddictPlayer.apk` is protected by a 360 Jiagu generation whose main Java/Dex payload is not statically exposed by ordinary JADX. Visible Java is largely loader/stub code. This Java layer remains **UNRESOLVED**, but it does not block the result because the relevant native USB library is present in plaintext and its native volume function was fully disassembled.
- The Android implementation uses libusb and the standard UAC2 Feature Unit transfer described above; it is not a UA4-specific HID report.
- The bundled Windows driver exposes standard KS audio topology interfaces and directly supports PID `3026`; there was no need to find or reproduce a separate exported Shanling volume DLL.
- UA4 HID is input-only at the logical report level for this purpose. It is not a suitable standard-HID output route for volume.

## Remaining unresolved items

- A physical live capture of candidate `0x02FE/0x02FF` reports is impossible before the approved onboard write. Scan directions and detent count remain the controlled-validation gate.
- Persistence and cross-mode behavior must be verified after the approved write with `Fn+A -> Fn+S -> Fn+A`, reconnect, and power cycle.
- The protected Eddict Java control flow and exact JNI caller are unresolved. The native implementation, command construction, range mapping, and transport are confirmed.
- The precise electrical bus used by the UA4 MCU to write ES9069Q registers 74/75 is not confirmed.

None of these items blocks the implemented architecture.

## Primary local evidence locations

- `Shanling_UA4_DAC/EddictPlayer.apk`
- `analysis/static/eddict_apk/lib/arm64-v8a/libusbaudio.so`
- `Shanling_UA4_DAC/Shanling_UA4.hex`
- `analysis/static/ua4_firmware.bin` (derived copy only)
- `analysis/static/shanling_driver/WHQL/ctaud.inf`
- `RK_M75_Keyboard/RK Keyboard Software/Cfg.ini`
- `RK_M75_Keyboard/RK Keyboard Software/Dev/0163/KB.ini`
- `analysis/LIVE_PROBE_RESULTS.md`
- `analysis/RK_OS_MODE_BANK_EVIDENCE.md`
- `analysis/RK_OS_MODE_TRACE_ANALYSIS.md`
- `docs/RK_M75_DUAL_BANK_PRE_WRITE.md`
- `tools/DeviceProbe`, `tools/UsbDescriptorProbe`, `tools/KsAudioProbe`, and `tools/CoreAudioProbe`
- `tools/disasm_elf.py`, `tools/disasm_8051.py`, `tools/elf_strings.py`, and `tools/intelhex_extract.py`

Original inputs were read only. Extracted and derived files were written only under `analysis`, and program/source outputs only under `src`, `tests`, `tools`, and `dist`.

## External corroborating sources

- Shanling UA4 introduction: <https://newen.shanling.com/article-IntroUA4.html>
- Shanling UA4 product article: <https://shanling.com/article/1170>
- ESS ES9069Q data sheet v0.2.2: <https://www.esstech.com/wp-content/uploads/2025/06/ES9069_Datasheet_v0.2.2.pdf>
- Microsoft Raw Input device registration: <https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-rawinputdevice>
- Microsoft low-level keyboard hook: <https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc>
