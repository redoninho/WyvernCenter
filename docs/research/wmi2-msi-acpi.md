# WMI v2 (`MSI_ACPI`): how MSI drives 11th-gen+ laptops

Static analysis on 2026-10-17. No method was called on hardware; the GF63 does not implement this interface (see `gf63-thin-10sc.md` §0.1).

Sources:

- `MSIWMIACPI2.dll` 1.0.2410.0101, SHA-256 `1930038a…6533828`. REA `inspect_managed_members` evidence `ev_28842872…434bf` gave the method inventory; the ILSpy output in `.re/ilspy` (same hash) gave the method bodies.
- Its callers in One Dragon Center: `API_NB_Base Module.dll`, `API_NB_Battery.dll`, `API_NB_System Monitor.dll` and `MSIAPService.exe`.
- `C:\Windows\SysWOW64\msiapcfg.dll`. REA `inspect_pe_resources` evidence `ev_48046ed0…1cd141a` found one `MOFDATA/MOFRESOURCE` resource of 3629 bytes.
- The live class definition, read with `ManagementClass("root\wmi","MSI_ACPI").GetText(Mof)`.
- msi-ec `msi-ec.c` (GPL-2.0-or-later), fetched 2026-10-17, for cross-checking.

## 1. Interface

| | |
|---|---|
| WMI class | `root\WMI : MSI_ACPI`, instance `ACPI\PNP0C14\0_0` |
| ACPI WMI GUID | `{ABBC0F6E-8EA1-11D1-00A0-C90629100000}` |
| Data block | `Package_32` `{ABBC0F63-8EA1-11D1-00A0-C90629100000}`, `uint8 Bytes[32]` |

Method IDs (`WmiMethodId`), as needed for a GUID-level call:

| ID | Method | ID | Method |
|---|---|---|---|
| 1 | GetPackage (16 B) | 2 | SetPackage |
| 3 | Get_EC (out only) | 4 | Set_EC |
| 5/6 | Get/Set_BIOS | 13/14 | Get/Set_Temperature |
| 15/16 | Get/Set_Thermal | 17/18 | Get/Set_Fan |
| 19/20 | Get/Set_Device | 21/22 | Get/Set_Power |
| 25/26 | Get/Set_AP | **27/28** | **Get/Set_Data** |
| 29 | Get_WMI (out only) | | |

**The class comes from MSI software, not the BIOS.** The MOF that defines it ships in `msiapcfg.dll`, registered as `HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi\MofImagePath = %windir%\sysWOW64\msiapcfg.dll`. MSI's library installs it when missing (`InstallService`) and then asks for a reboot.

So on a WMI2 laptop that has never had MSI Center installed, `MSI_ACPI` may not exist as a WMI class even though the firmware implements the GUID. Wyvern must not depend on that class existing. Instead it should call by GUID through `WmiOpenBlock` + `WmiExecuteMethodW` (advapi32, documented in the WDK `wmium.h`), which needs no MOF, or register its own MOF as a fallback.

## 2. Call protocol

All calls run under one process-wide lock. MSI uses `ManagementObject.InvokeMethod` with a cloned in-params object.

- **Input:** 32 bytes. `[0]` = the parameter (an EC offset for `Get_Data`/`Set_Data`, or a block index for the typed methods). For a Set call, data starts at `[1]`. `Get_WMI`, `Get_EC` and `GetPackage` take no input.
- **Output:** 32 bytes. `[0]` = status flag (**1 = OK**); payload starts at `[1]`. MSI's `InvokeResults.Data` is the output shifted by one, so `Data[i]` = `out[i+1]`.
- **Get_Data(offset):** 1 byte, one EC RAM byte at `offset` (0–255).
- **Set_Data(offset, value):** writes one byte. Success = flag `1`.
- **Get_WMI:** `out[1]` = major version, `out[2]` = minor version. WMI2 means major ≥ 2.
- **Get_EC:** valid when the output is at least 30 bytes and `out[0] == 1`.
  - `out[1]` bit 7 = "TigerLake-class" platform, bit 6 = EC RAM page changed, bits 5–4 = EC RAM main version, bits 3–0 = sub version.
  - `out[2..29]` = ASCII EC firmware version and build date. This is the same firmware ID string msi-ec keys on.
- MSI treats the interface as usable only when major ≥ 2 **and** the TigerLake bit is set. It caches the result in `HKLM\SOFTWARE\WOW6432Node\MSI\NB\WMIEC` (empty on the GF63) and re-reads it after each reboot.
- Retry pattern used everywhere: on failure, wait 1 s and try once more.

## 3. Settings MSI writes on WMI2 laptops

These are fixed addresses with **no per-model table** in MSI's code. Each feature checks `WmiMajorVersion >= 2` and uses the same addresses on every WMI2 machine. The "WMI1 equivalent" column is the address the same code uses on the old path, i.e. the GF63.

| Feature | Read | Write | Bits / values | WMI1 equivalent |
|---|---|---|---|---|
| Shift (performance) mode | `Get_AP(0).Data[2]` | `Set_Data(0xD2, v)` | same codes as WMI1 (C1/C2/C4) | `0xF2` |
| Fan mode | `Get_AP(1).Data[0]` | `Set_Data(0xD4, v)` | silent = set bit 4; custom curve = set bit 6 **and clear bit 7** | `0xF4` |
| Keyboard backlight | `Get_AP(0).Data[3]` (&3 = level) | `Set_Data(0xD3, v)` | | `0xF3` |
| Cooler Boost | `Get_Thermal(3).Data[0]` | `Set_Data(0x98, v)` | bit 7 | `0x98` |
| Battery charge limit | `Get_Data(0xD7)` | `Set_Data(0xD7, v)` | keep bit 7, low 7 bits = 100 / 80 / 60 % | `0xEF` |
| Fn/Win swap | `Get_BIOS(1).Data[0]` | `Set_Data(0xE8, v)` | bit 4 (newer models also store a UEFI variable `MsiDCVarData`) | `0xBF` |
| Webcam | `Get_Device(1).Data[0]` | `Set_Data(0x2E, v)` | bit 1 | `0x2E` |
| USB power share | `Get_Device(1).Data[1]` | `Set_Data(0x2F, v)` | bit 2 | |
| Keyboard backlight auto-off | `Get_Device(1).Data[2]` | `Set_Data(0x2C, v)` | bit 3 | |
| Graphics switch (MUX) | `Get_AP(0).Data[1]` | `Set_Data(0xD1, v)` | bits 0–1 | `MSI_System[4]` |
| Fan curves | `Get_Fan(1)` CPU / `Get_Fan(2)` GPU | `Set_Fan(1/2, Data)` | `Data[1..6]` = 6 curve points; the method hides the EC address | CPU/VGA[11..16] |
| Fan RPM | `Get_Fan(0)` | | `Data[0..1]` CPU, `Data[2..3]` GPU (raw counters) | MSI_AP 2–5 |
| Temperatures | `Get_Temperature(1)` CPU / `(2)` GPU | | `Data[0]` current, `Data[3..7]` curve temperatures | |
| Other writes seen | | `0xBF` bit 0 (System Monitor), `0xDB` (LED/scene codes), `0xBE` (VGA BIOS), `0xD9` bit 0 (set by the MSI service on start) | | |

Pattern: the WMI1 `0xF1–0xF4` block moved to `0xD1–0xD4`. `Get_AP(0)` reads `0xD0–0xD5` and `Get_AP(1)` starts at `0xD4`; both are inferred from the shift, keyboard and MUX positions.

## 4. Cross-check against msi-ec

msi-ec's 21 layouts reduce to **two address families**:

| Family | Layouts | Battery | Fn/Win | Cooler Boost | Shift | Fan | Webcam |
|---|---|---|---|---|---|---|---|
| G1 (WMI1, older) | G1_0…G1_13 | 0xEF | 0xBF | 0x98 | 0xF2 | 0xF4 | 0x2E |
| G2 (WMI2, 11th gen+) | G2_0…G2_10 (~250 firmware IDs incl. Katana, Pulse, Sword, Cyborg, Stealth, Raider) | 0xD7 | 0xE8 | 0x98 | 0xD2 | 0xD4 | 0x2E |

- Every G2 address matches MSI's own WMI2 code in §3 exactly.
- Every G2 layout uses the same mode values: shift C1/C2/C4, fan 0D/1D/8D.
- G2 layouts differ mainly in extras: keyboard backlight at 0xD3 or unsupported, and super battery at 0xEB.

**Conclusion:** one Wyvern "WMI2 layout" plus capability checks should cover the large majority of current MSI laptops. Per-model data is only needed for extras and quirks.

## 5. Open questions (need a WMI2 tester)

1. **Custom fan mode value.** MSI Center sets bit 6 and clears bit 7 of `0xD4` (`0x4D`). msi-ec says advanced = bit 7 (`0x8D`). Read `0xD4` on a tester's laptop after choosing each fan mode in MSI Center.
2. **Fan-curve scale and temperature points.** `Set_Fan` takes 6 points. Confirm the 0–150 scale assumption and whether the temperature thresholds are writable via `Set_Temperature`.
3. **MOF presence.** Does `MSI_ACPI` exist on a WMI2 laptop with no MSI software? Decides whether a GUID-level call is required or only nicer.
4. **The `Get_EC` TigerLake bit on AMD WMI2 machines.** MSI also gates on CPU generation for Intel (≤10th gen = not supported).
5. Whether `0xD9` bit 0, which the MSI service sets on start, is something Wyvern also needs to set.

## 6. What Wyvern does with this (branch `wmi2-transport`, 2026-10-17)

**Firmware access by GUID, with no MSI software needed.** The GF63's SCM0 device carries no class definitions of its own either: no `WQxx` MOF block in `Device (SCM0)`. So the `MSI_System`/`MSI_CPU`/… classes, like `MSI_ACPI`, exist only because MSI's `msiapcfg.dll` is installed.

`AcpiWmiBlock` opens a firmware block by GUID through advapi32 (`WmiOpenBlock`, `WmiQuerySingleInstanceW`, `WmiSetSingleInstanceW`, `WmiExecuteMethodW`). That talks to wmiacpi.sys directly and only needs administrator rights.

- **Verified on the GF63 (2026-10-17, `msi-probe guid`):** every SCM0 value read by GUID equals the class route.
- On the same machine, `MSI_ACPI`'s GUID reports `4200` (not in this firmware). That's correct for a WMI1 laptop, even though the class is registered: the firmware's GUIDs and the class definitions are separate.

**Detection order** (in `AppController`):

1. MSI's SCM0 classes, the route proven on the GF63.
2. SCM0 by GUID (`GuidScmTransport`), when the classes are missing.
3. WMI v2 by GUID (`GuidWmi2Transport`, then `Wmi2Ec.Detect`: Get_WMI ≥ 2, Get_EC firmware).

A WMI2 laptop resolves to `Profiles/generic-msi-wmi2.json`. That profile uses the §3 addresses, with sensors at 0x68/0x71/0x80/0x89 and tachometers from Get_Fan(0).

**Read-only beta.** Three separate locks keep WMI2 laptops from being written:

- `Wmi2Ec.WritesEnabled` is false.
- Every WMI2 setting is `readOnly`.
- The app passes `canWrite: false` for WMI2.

Support reports (and `msi-probe wmi2`) dump Get_WMI, Get_EC, all 256 EC RAM bytes, MSI's typed blocks, and a plain reading of the fan-mode byte.

### Plan for the custom fan curve on WMI2 (write phase)

The curve itself is written with MSI's own method, `Set_Fan(1/2)` with `Data[1..6]` = speeds, so its EC address doesn't matter. Only the custom fan mode value is open: 0x4D per MSI Center's code, 0x8D per msi-ec. Wyvern settles it per laptop instead of guessing:

1. **Ask the report first.** If MSI Center's custom fan mode was on when a tester made a report, the 0xD4 line already says which bit this firmware uses.
2. **Otherwise run a self-check** (only fans *faster*, the safe direction, with the thermal failsafe armed):
   - Save 0xD4 and both curves.
   - Write a curve whose lowest points are clearly higher than now.
   - Set MSI Center's own value (0x4D).
   - Read 0xD4 and `Get_Fan(1)` back, then watch the Get_Fan(0) tachometers for about 15 s.
   - If the fans speed up, that encoding is confirmed for this firmware.
   - If not, put everything back and try 0x8D the same way.
3. **If neither works**, custom curves stay off on that laptop (modes, Cooler Boost and battery limit are unaffected). The originals are always restored, and the result is stored per EC firmware so the check runs once.

**Built (2026-10-21) as the wizard's "Custom fan curves (measured)" step** (`FanCurveCheck`, `VerifySession.RaiseCurves`):

- Curve points on WMI2 are now `Get_Fan(1|2)` bytes 1–6 (MSI's own read). Writes go through `Wmi2Ec.WriteFanCurvePoint`: read the block, change one point, write MSI's 8-byte payload back with `Set_Fan` (layout checked in MSI's `UserScenario` and `WmiAcpiLib2.Set`). These are the only writable block bytes (`Wmi2Block.Writable`), still through `EcController` (whitelist 0–150, snapshot, read-back).
- The test curve is each curve's own top point on every point, so no scale is assumed. Order: the CPU fan as it is (6 s), raise both curves + candidate mode, watch 15 s. Faster = +15 % and +300 RPM. Otherwise the mode goes back, the fans settle for 10 s, and the next candidate runs. The encoding already in 0xD4 goes first, else 0x4D.
- Fan mode goes back first, then the curves; closing the wizard does the same. The report gets the before/peak RPM per candidate.

## 7. MSI's service bridge removed (2026-10-17)

Wyvern used to reach Cooler Boost, the charge limit and Fn/Win on the GF63 through MSI Foundation Service's named pipe `MSI_SERVICE_2`, with MSI's 3DES key recipe. That route is gone:

- **Test on the GF63:** with the service started (`MSIAPService.exe` 1.1.2408.1501), a **non-admin** process could open the pipe.
- **Public reports:** CVE-2026-37453 (insecure pipe permissions) and CVE-2026-37454 (the 3DES-ECB scheme), plus a public write-up of SYSTEM escalation through the same pipe.
- **Why that matters:** starting that service, as the bridge did on demand, opens the hole while it runs, and shipping the key recipe would document it.

What replaced it:

- **Cooler Boost** is now Wyvern's **Max Cooling**: both fan curves to full speed through the firmware, held by `CoolingHold` together with the thermal failsafe.
- **The charge limit** remains only where the firmware exposes it (WMI2 `0xD7`).
- **Webcam, Win-key lock and Fn⇄Win** were removed as unfinished. Ideas for later: webcam `0x2E` bit 1; Win-key lock `0x2B` bit 6 (GF63); Fn⇄Win `0xBF` bit 4 (WMI1) / `0xE8` bit 4 (WMI2), plus the `MsiDCVarData` UEFI variable.

**Still being shipped after the fix.** MSI says the fix is in MSI Center 2.0.70.0 (June 2026). This GF63 shows MSI was still installing the vulnerable build afterwards:

- Dragon Center 2.0.156.0 was installed 2026-07-03.
- "MSI NBFoundation Service" 1.0.2411.1201 was installed 2026-07-23; its `MSIAPService.exe` is 1.1.2408.1501, built 2024-08-15.
- Both dates are after MSI's fix and after the 2026-07-01 disclosure. The pipe accepts non-admin clients.

In the app, the charge limit on WMI1 laptops is greyed out with this reason.
