# Adding a laptop model

Wyvern Center gives full control only when it has a **model profile** for that exact EC firmware. Other MSI laptops are
read-only unless their owner turns on experimental mode, which uses the same board's profile or the cautious generic
one. A profile is a JSON file in [`src/WyvernCenter.Core/Profiles/`](../src/WyvernCenter.Core/Profiles/) that lists:
- every setting Wyvern Center may touch;
- where each setting lives;
- which values it may take;
- whether that has been seen working on real hardware.

## If you own the laptop

1. Install Wyvern Center and run it as administrator. On an unknown model it starts read-only.
2. Open **Settings → This laptop → Create support report**. A folder opens containing:
   - `report.md`: the model, BIOS and EC firmware version, and every value the firmware's `SCM0` interface reports.
     Battery blocks are left out because they can contain serial numbers.
   - `dsdt.aml`: the laptop's ACPI DSDT table. It maps each `SCM0` value to an EC register.
3. Open a **Support my laptop** issue, paste `report.md` and attach `dsdt.aml`.
4. If you can, also write down what MSI Center's modes change. Switch each mode in MSI Center with Wyvern Center's Monitor
   open, or run `tools/MsiProbe watch` (see below) while you switch.

## Writing the profile (maintainers)

### 1. Map `SCM0` to EC offsets

Decompile the DSDT with Intel's ACPICA:

```
iasl -d dsdt.aml
```

Find `Device (SCM0)` and its `Field` declarations over the EC operation region. Each `WQAx(index)` (read) and
`WSAx(index, value)` (write) handles one field. Work out the **computed** offset of each field, not its name: names
like `TDF4` don't always match where the field actually sits.
[research/gf63-thin-10sc.md §0.4](research/gf63-thin-10sc.md) shows the full table for the GF63.

The usual meanings are below, by EC offset. They agree with MSI's own software and with the Linux
[msi-ec](https://github.com/BeardOverflow/msi-ec) project; confirm them for your model.

| EC offset | Meaning |
|---|---|
| `0x68` / `0x80` | CPU / GPU temperature (°C) |
| `0x71` / `0x89` | CPU / GPU fan speed |
| `0x69`–`0x6E`, `0x72`–`0x77` | CPU fan curve: temperature points, speed points |
| `0x81`–`0x86`, `0x8A`–`0x8F` | GPU fan curve |
| `0xF2` | Performance (shift) mode: `0xC0`–`0xC4` |
| `0xF3` | Keyboard backlight, low bits |
| `0xF4` | Fan mode: auto `0x0D`, silent `0x1D`, advanced `0x8D` |
| `0x98` bit 7 | Cooler Boost (not in `SCM0` on WMI1 laptops; Wyvern's Max Cooling uses the fan curves instead) |
| `0xEF` low 7 bits | Battery charge limit (not in `SCM0` on WMI1 laptops; WMI2 laptops have it at `0xD7`) |

### 2. Watch what MSI Center does

`tools/MsiProbe` is read-only. Run it as administrator:

```
dotnet run --project tools/MsiProbe -- dump                       # every SCM0 value once
dotnet run --project tools/MsiProbe -- watch --seconds 120        # print every change while you click through MSI Center
dotnet run --project tools/MsiProbe -- guid                       # the same values read by firmware GUID
dotnet run --project tools/MsiProbe -- wmi2                       # 11th gen and newer: WMI v2 version, EC RAM, MSI's blocks
```

A setting is `"verified": true` only when you have seen it change on real hardware. That means watching MSI Center
change it, or seeing that a write reads back and does what it should. Leave anything else `false`: Wyvern Center won't
write it.

### 3. Write the JSON

Copy [`gf63-thin-10sc.json`](../src/WyvernCenter.Core/Profiles/gf63-thin-10sc.json) and change it:

```jsonc
{
  "name": "MSI GF63 Thin 10SC",
  "firmware": ["16R5EMS1.102"],          // exact EC firmware versions this profile is checked on
  "msiServices": ["MSI Foundation Service", ...],   // MSI services the footer checks (they can override Wyvern Center)
  "curve": { "minSpeed": 0, "maxSpeed": 150, "floorAtIndex": 5, "floorSpeed": 100 },
  "settings": {
    "shiftMode": {
      "route": "scm", "class": "System", "index": 7,  // SCM0 class and instance
      "mask": "0xFF",
      "values": { "comfort": "0xC1", "eco": "0xC2", "turbo": "0xC4" },
      "verified": true,
      "note": "EC 0xF2. How it was checked."
    },
    "kbdBacklight": {
      "route": "scm", "class": "System", "index": 8,
      "mask": "0x03", "min": 0, "max": 3, "verified": true
    }
  }
}
```

The fields:

- **`mask`**: the bits this setting owns. Values are written shifted down, so a `0x80` mask takes 0 or 1, and the
  other bits of the byte are kept.
- **`values` or `min`/`max`**: the only values that may be written.
- **`readOnly: true`**: a sensor. It is never written.
- **`expect`** (optional): `{ "mask": "0xFF", "values": [...] }`. The current byte, masked, must be one of these
  values before a write. The generic profile uses this to refuse writing a register that isn't what it expects.
- **`curve.maxSpeed`**: full fan speed on this model's scale. It is 150 on the GF63; some models use 100.
- **Setting names:** the app looks settings up by name. Keep the names used in the GF63 file: `shiftMode`,
  `fanMode`, `kbdBacklight`, `cpuTemp`, `cpuCurveSpeed0`–`5`, `coolerBoost`, `chargeLimit` and so on.

### 4. Test

- `dotnet test` must pass. The catalog tests load every profile.
- On the laptop:
  1. Switch every profile.
  2. Edit a fan curve.
  3. Toggle each control, then use **Restore original settings**.
  4. Confirm the values in MSI Center or with `MsiProbe dump` match what you expected.

Open a pull request with the JSON, a short note on how each verified setting was checked, and the `report.md`.
