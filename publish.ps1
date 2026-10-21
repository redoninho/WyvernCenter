# Builds the self-contained single-file WyvernCenter.exe into .\dist (the only build that can be installed for startup).
# No native self-extraction: extracted DLLs would land in a user-writable temp folder and load elevated.
# No compression: compressed assemblies are inflated into private memory (~60 MB more RAM).
$ErrorActionPreference = 'Stop'
dotnet publish "$PSScriptRoot\src\WyvernCenter\WyvernCenter.csproj" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none `
  -o "$PSScriptRoot\dist"
