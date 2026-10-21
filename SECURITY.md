# Security

Wyvern Center runs as administrator and writes to laptop firmware. Security matters here, and reports are welcome.

## Reporting

Please **don't open a public issue** for a vulnerability. Use GitHub's **Report a vulnerability** button: Security tab →
Advisories. Include the steps to reproduce and the version.

## In scope

- Ways a non-admin user or process could make Wyvern Center run code, load a DLL, write a file or change firmware values
  with its admin rights. Examples: planted DLLs or executables, links or junctions, files in user-writable folders.
- Ways to make it write values outside a model profile's whitelist, or to skip the original-value snapshot.
- Ways to stop or delay the thermal failsafe.

## Design notes

- The installed copy and its data live in `C:\Program Files\Wyvern Center`, which only administrators can change. The data
  folder is refused if it's a link.
- The startup task points only at that installed copy, never at a build in a user folder.
- Native libraries load from System32 (NVML: System32 or NVIDIA's NVSMI folder) by full path, and programs start by
  full path.
- The support report is written inside the data folder, which only administrators can change; your account can
  read it. It is never written by the elevated process to a user-writable folder like the Desktop.
