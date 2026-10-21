# Contributing

Thanks for helping. The most useful contribution is a **model profile** for a laptop you own: see
[docs/adding-a-model.md](docs/adding-a-model.md).

## Ground rules

Wyvern Center runs elevated and writes to laptop firmware. Changes are held to a few hard rules:

- **No drivers, no services, nothing installed** beyond the optional copy in Program Files for startup.
- **Whitelist everything.** A setting is written only if a model profile lists it, with allowed values, and it has been
  verified on hardware.
- **Fail closed.** If the snapshot of original values, the data folder or the firmware interface isn't trustworthy,
  don't write at all.
- **The thermal failsafe never waits.** Nothing may block it: no slow I/O, no MSI service call, no UI thread.
- **Elevated-process hygiene:**
  - load native DLLs only from System32 (or NVIDIA's admin-only folder for NVML);
  - start programs by full path;
  - never write files into folders a normal user can change.
- **Lean in the background.** The tray app should stay around 20 MB and near 0% CPU. Hardware polling that only
  windows need stops when the windows close.

## Development

- .NET 8 SDK on Windows. `dotnet test` runs the unit tests, which use fakes, not hardware.
- Write the test first for any behaviour change in `WyvernCenter.Core`.
- Keep the style of the surrounding code: small classes, comments that explain *why*, plain wording in the UI.
- Run `.\publish.ps1` to build the single-file exe in `dist\`.

## Pull requests

- Keep each pull request to one change.
- Describe how you tested it. For hardware changes, give the laptop model and EC firmware, and what you observed.
- CI must pass.

By contributing you agree that your contribution is licensed under the [GPL-3.0](LICENSE).
