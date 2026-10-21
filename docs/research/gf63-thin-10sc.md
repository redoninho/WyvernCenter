# Phase 0 findings: GF63 Thin 10SC (MS-16R5, BIOS E16R5IMS.103)

Written in our own words from static analysis. No MSI code is reproduced here; decompiled sources live in `.re/` (gitignored).

## §0 Static analysis (Task 0)

### 0.1 `MSI_ACPI` is NOT implemented on this laptop

- The class `root\WMI : MSI_ACPI` (GUID `{ABBC0F6E-8EA1-11d1-00A0-C90629100000}`) is registered in Windows, but **that GUID appears in no ACPI table** (checked DSDT, SSDT, SSD1–SSD7). With no firmware object behind it, the calls cannot work.
- MSI's own log (`C:\Program Files (x86)\MSI\NoteBook\MSI NBFoundation Service\WmiAcpi2.log`) shows every MSI module getting `Get_WMI → NotSupported` on this machine, then logging "WMIACPI2 only supports Intel TigerLake (11th gen.) or newer". The registry cache `HKLM\SOFTWARE\WOW6432Node\MSI\NB\WMIEC` is empty for the same reason.
- **Consequence:** spec §2's "MSI's own WMI interface" choice is wrong for this laptop. `MSI_ACPI` (WMI v2, a 32-byte package with `Get_Data(addr)`/`Set_Data(addr,val)`) is how MSI drives 11th-gen and newer machines. This machine is on MSI's "v1" path.

For reference, on v2 machines MSI's library reads `Get_WMI` byte 1–2 = WMI major/minor version, and `Get_EC` byte 0 = status (1 = ok), byte 1 = EC RAM layout flags, bytes 2–29 = ASCII EC firmware version + build date. `Get_Data`/`Set_Data` take an EC offset (0–255) and one data byte.

### 0.2 What the firmware actually exposes: `SCM0` WMI data blocks

The DSDT has a device `SCM0` whose `_WDG` declares 9 data blocks plus 1 event. Each matches a `root\WMI` class whose only property is one read/write value per instance:

| Object ID | Class | Instances | Value type |
|---|---|---|---|
| AA | `MSI_Software` | 34 | uint8 |
| AB | `MSI_Device` | 4 | uint8 |
| AC | `MSI_Power` | 3 | uint8 |
| AD | `MSI_Master_Battery` | 16 | sint16 |
| AE | `MSI_Slave_Battery` | 14 | sint16 |
| AF | `MSI_CPU` | 19 | uint8 |
| AG | `MSI_VGA` | 18 | uint8 |
| AH | `MSI_System` | 21 | uint8 |
| AI | `MSI_AP` | 8 | uint8 |
| C0 (event) | `MSI_Event` | 1 | `MSIEvt` uint32 |

All have read (`WQAx`) and write (`WSAx`) methods in the DSDT. **This is a firmware-backed, driverless interface.** What each instance maps to (EC field) is the open question that the DSDT decompile (iasl) answers.

Known meanings from MSI's System Monitor module (it reads/writes these through `MSIService.exe` over the named pipe `\\.\pipe\simple` with text commands like `root\WMI:Q:MSI_Device:Device:<index>` / `...:S:...:<index>:<value>`):

| Class[index] | Bit / value | Meaning |
|---|---|---|
| `MSI_Device[0]` | bit 0 | Bluetooth |
| `MSI_Device[0]` | bit 1 | Webcam |
| `MSI_Device[0]` | bit 3 | WLAN |
| `MSI_Device[2]` | bit 6 | Keyboard lock (Win key) |
| `MSI_Device[2]` | bit 7 | Touchpad |
| `MSI_System[0]` | bit 2 | "Turbo battery" |
| `MSI_System[1]` | bit 0 | ECO switch |
| `MSI_System[4]` | bits 0–1 | Graphics switch |
| `MSI_System[15]` | value | Volume |
| `MSI_Software[0]` | bit 0 | SCM installed flag |

### 0.3 How MSI Center does fans / modes / battery on this laptop: raw EC RAM via a driver

On v1 machines MSI's feature modules read and write the EC's memory-mapped RAM window at physical address **`0xFC000800` + offset**, through `MSIAPService` (runs as SYSTEM and uses a WinIO kernel driver for physical-memory access), which they reach over a named pipe. Accesses are **32-bit**: MSI reads 4 bytes, replaces the low byte's bits it cares about, and writes 4 bytes back.

Offsets seen in MSI's code (agree with the Linux `msi-ec` project's maps):

| Offset | Used by | Meaning |
|---|---|---|
| `0x2C` | Win/Fn change | (to confirm; Fn/Win related) |
| `0xBF` | Win/Fn change | Fn/Win swap (bit 4 per msi-ec) |
| `0xEF` | Battery | charge limit: low 7 bits = percent (100/80/60), bit 7 preserved |
| `0xF3` | User Scenario, System Monitor | keyboard backlight level (low bits) |
| `0xF4` | User Scenario | fan mode |
| `0xF7` | General settings | (to confirm) |
| more via variables | User Scenario | shift mode (msi-ec: `0xF2`), Cooler Boost (msi-ec: `0x98` bit 7), fan curve tables: to extract |

Physical-memory access needs a kernel driver, which the user ruled out (spec: no drivers). The only driverless routes to these offsets are (a) the `SCM0` blocks, if their firmware methods expose these EC fields, or (b) asking MSI's own `MSIAPService` to do it over its pipe (keeps one MSI service running).

### 0.4 DSDT decompile: every `SCM0` instance → EC offset

Decompiled with Intel ACPICA `iasl` 20260408 (`iasl -d DSDT.aml`). `SCM0` lives at `\_SB.PCI0.LPCB.EC.SCM0` and declares its own `Field` over the EC `OperationRegion` (`EmbeddedControl`, 0x00–0xFF, byte access). Each `WQAx(index)` returns one field and each `WSAx(index, value)` writes the **same** field: reads and writes mirror exactly, for every block.

Field names look like offsets (`TDF4`, …) but **the computed offset is what counts**: `TDBE`/`TDBF` actually sit at `0xBC`/`0xBD` (verified in the raw AML: no gap after `TDBB`). The field list **deliberately skips `0xEF`** (reserved 8 bits after `TDEE`), and has no field at `0x98` or `0xBF`.

| Class | Index → EC offset |
|---|---|
| `MSI_System` | 0 → `0xE4` bits 0–2 · 1 → `0xE4` bits 3–7 · 2 → `0xE7` · 3 → `0xF0` · 4 → `0xF1` · 5 → `0xFE` · 6 → `0xFF` · **7 → `0xF2`** · **8 → `0xF3`** · **9 → `0xF4`** · 10–18 → `0xF5`–`0xFD` · 19 → `0xE0` · 20 → `0xE3` |
| `MSI_CPU` | 0 → `0x33` · **1 → `0x68`** · **2 → `0x71`** · 3 → `0x70` · **4–10 → `0x69`–`0x6F`** · **11–18 → `0x72`–`0x79`** |
| `MSI_VGA` | 0 → `0x33` · **1 → `0x80`** · **2 → `0x89`** · 3 → `0x88` · **4–10 → `0x81`–`0x87`** · **11–17 → `0x8A`–`0x90`** |
| `MSI_Device` | 0 → `0x2E` · 1 → `0x2F` · 2 → `0x2B` · 3 → `0x2C` |
| `MSI_Power` | 0 → `0x30` · 1 → `0xBC` · 2 → `0xBD` |
| `MSI_Software` | 0 → `0x2D` bit 0 · 1 → `0x2D` bits 1–4 · 2 → `0xEE` · 3 → `0x2D` bits 5–7 · 4 → `0xE5` · 5 → `0xE6` · 6–33 → `0xA0`–`0xBB` |
| `MSI_AP` | 0–7 → `0xCF` down to `0xC8` |
| `MSI_Master_Battery` | 0 → `0x31` · 1–11 → `0x38`–`0x4C` (16-bit) · 12 → `0xE8` · 13 → `0x35` · 14 → `0x36` · 15 → `0x4E` (16-bit) |
| `MSI_Slave_Battery` | 0 → `0x32` · 1–11 → `0x50`–`0x64` (16-bit) · 12 → `0xE9` · 13 → `0x66` (16-bit) |

### 0.5 Settings: how MSI drives them on this (v1) laptop, and what is driverless

From MSI's Base Module (`Shift`, `UserScenario`), General Settings, System Monitor and Battery code, cross-checked against §0.4:

| Setting | EC offset | MSI's v1 route | Encoding (from MSI's code) | Driverless via SCM0? |
|---|---|---|---|---|
| **Shift / performance mode** | `0xF2` | `MSI_System[7]` (WMI) | bits 7–6 = `11` (active); low bits select: `0xC0`, `0xC1`, `0xC2`, `0xC3`, `0xC4`. MSI's enum: +0 Comfort, +1 Green, +2 ECO, +3 User, +4 Sport. Which of these the GF63 accepts and which MSI Center mode uses which: **to confirm on hardware** | **Yes** |
| **Fan mode** | `0xF4` | `MSI_System[9]` (WMI) | Auto: clear bits 7–6. Advanced (custom curve): set bit 7, clear bit 6 | **Yes** |
| **CPU fan curve** | temps `0x69`–`0x6E`, speeds `0x72`–`0x77` | `MSI_CPU[4..9]`, `MSI_CPU[11..16]` (WMI) | 6 temperature points + 6 fan-speed points | **Yes** |
| **GPU fan curve** | temps `0x81`–`0x86`, speeds `0x8A`–`0x8F` | `MSI_VGA[4..9]`, `MSI_VGA[11..16]` (WMI) | same shape | **Yes** |
| **CPU / GPU temp** | `0x68` / `0x80` | `MSI_CPU[1]` / `MSI_VGA[1]` | °C | **Yes** (read) |
| **Fan speed** | `0x71` / `0x89` | `MSI_CPU[2]` / `MSI_VGA[2]` | units to confirm (msi-ec: percent) | **Yes** (read) |
| **Keyboard backlight** | `0xF3` | raw EC RAM (driver) in MSI's code, but `0xF3` is `MSI_System[8]` | low bits = level; preserve high bits | **Yes** (SCM0 reaches it) |
| **Webcam** | `0x2E` bit 1 | `MSI_Device[0]` | bit 1 = on | **Yes** |
| **Win-key lock** | `0x2B` bit 6 | `MSI_Device[2]` | bit 6 | **Yes** |
| Touchpad / Wi-Fi / BT | `0x2B` bit 7 / `0x2E` bits 3, 0 | `MSI_Device` | bits | Yes (not v1 scope) |
| **Cooler Boost** | `0x98` bit 7 | raw EC RAM via `MSIAPService` + WinIO driver | bit 7 = on | **No**: no SCM0 field |
| **Battery charge limit** | `0xEF` | raw EC RAM via `MSIAPService` + WinIO driver | bits 0–6 = percent (100/80/60), keep bit 7 | **No**: firmware skips `0xEF` |
| **Fn/Win swap** | `0xBF` bit 4 (msi-ec) | raw EC RAM (`0xBF`) | bit | **No**: no SCM0 field |

**How MSI writes through SCM0:** it enumerates `SELECT * FROM MSI_System` (etc.), takes the Nth `ManagementObject`, `SetPropertyValue("System", v)`, `Put()`. Relying on enumeration order is fragile; M-Helper should address instances by `InstanceName` (format to confirm in the probe).

### 0.6 Consequences for the design

- **Driverless covers most of v1:** modes, fan mode, both fan curves, temps, fan speeds, backlight, webcam, Win-key lock.
- **Three features need physical EC RAM access** (Cooler Boost, battery limit, Fn/Win swap), which the firmware does not expose. Options are a user decision (see the Task 0 report).
- **The probe must target the SCM0 classes**, not `MSI_ACPI`. Plan Tasks 1–4 (built around `MSI_ACPI` and 32-byte packages) need rewriting: the probe becomes "read every instance of the 9 classes", which is simpler.

## §1 Hardware reads (2026-10-14, read-only, `msi-probe`)

- SCM0 dump via `WmiScmTransport` matches the PowerShell dump (`raw/scm-dump-1.txt`); instance names `ACPI\PNP0C14\0_N` = index N. EC firmware: **`16R5EMS1.102 03112021 17:10:24`** → profile "MSI GF63 Thin 10SC" matched.
- **EC bridge works** (MSI Foundation Service pipe, handshake as "M-Helper", 3DES frames). Proof of offset base: bridge `0xF2` = `0xC1`, `0xF4` = `0x8D`, dword at `0xF2` = `0x008D80C1` (F3 = `0x80`): all identical to SCM0 `System[7]`, `[9]`, `[8]`.
- Current values: Cooler Boost `0x98` = `0x02` (bit 7 off) · battery `0xEF` = `0xD0` (bit 7 on + 80 % → MSI "Balanced") · Fn/Win `0xBF` = `0x28` (bit 4 off) · shift `0xC1` · fan mode `0x8D` (advanced) · backlight `0x80` (level 0).
- Thermal: CPU **94–95 °C** at both reads with CPU fan at 150 (curve max) under the user's normal load → failsafe threshold set at 95 °C (spec R1.3).
- Battery encoding (MSI code + reading): `0xEF` = `0x80 | percent`; MSI writes 100 / 80 / 60. M-Helper keeps bit 7 and writes the low 7 bits.

Raw: `raw/probe-session-1.log`.

## §2 MSI Center confirmation session (2026-10-14 00:49–00:51, `raw/session3-watch.log`)

| MSI Center action | Observed writes |
|---|---|
| Extreme Performance | shift `0xF2` C1→**C4**, fan mode `0xF4` 8D→**0D**, CPU+GPU curve speeds [12..16] lowered (auto-mode table) |
| Balanced | shift **C1**, fan mode **8D** (advanced) |
| Silent | fan mode **1D** (shift stays C1) |
| Super Battery | shift **C2**, fan **0D**, backlight `0xF3` 83→**80** |
| Back to Balanced | shift C1, fan 8D, curve speeds restored, backlight 80→83 |
| Cooler Boost on | `0x98` 02→**82** (bit 7) — never turned off in the session |
| Battery Master | `0xEF` D0 (80) → E4 (100) → D0 (80) → BC (60): bit 7 kept, low 7 bits = percent |
| Fn keys | no `MSI_Event` at all; `MSI_Device[0]` bit 6 toggles 19↔59 around scenario changes (meaning unknown) |

Verified in profile: shiftMode {C1, C2, C4}, fanMode {0D, 1D, 8D}, kbdBacklight, coolerBoost, chargeLimit, CPU/GPU curve speeds. Not verified (not observed): webcam, winKeyLock, fnWinSwap. `System[11]` (`0xF6`) 03→05 during Super Battery: unknown, untouched.
Hotkeys: Fn combos don't raise WMI events with MSI services running, so they're handled by the EC; M-Helper doesn't need to take them over (Task 10 dropped).

## §3 v3 hardware check (to do with the user present; spec §9)

1. Max clock: set 2.0 / 3.0 GHz caps, run Test, confirm the measured clock respects each cap. If not, switch `ProfileApplier` to `frequencyCapWorks: false` (fallback: caps below 2.5 GHz via max processor state).
2. Firmware levels: Test the same profile on Normal vs Super Battery vs Extreme; record W / GHz / °C here.
3. Each preset: switch and confirm the curve registers change (`msi-probe watch` or the live marker) and the fans sound right.
4. Max fans → Reset preset → fans return to the preset curve (the v0.2.0 bug).

## §4 GPU (2026-10-14, `nvidia-smi`, elevated, user present)

- GTX 1650 with Max-Q, driver 581.80. Power limit range reported 1–40 W (default 35, current 40); max graphics clock 1740 MHz, memory 5001 MHz.
- `nvidia-smi -pl 30` → "Changing power management limit is not supported for GPU": **power limit blocked** on this laptop.
- `nvidia-smi -lgc 300,1200` → "GPU clocks set": **clock lock works**; `-rgc` resets. Used by v3.1 profiles (GPU max clock).
- v3.1 checks still to do: cap 1200 MHz while gaming → Monitor ≤ 1200 MHz; GPU still sleeps on the desktop with Wyvern Center running and the cap is back after it wakes; Ctrl+Alt+P cycles profiles from a game and VS Code without stealing focus.
