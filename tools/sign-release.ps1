# Signs a GitHub release of Wyvern Center so the in-app updater will offer it (see UpdateSignature.cs).
#
#   .\tools\sign-release.ps1 v0.5.5
#
# Run it on the maintainer's PC after the Release workflow has finished. It downloads the release zip, checks the build
# attestation (built by this repository's workflow), hashes the exe itself (it never trusts the .sha256 files for what it
# signs), signs "tag + exe hash" with the release key and uploads WyvernCenter.exe.sig to the release.
#
# The private key lives only in %APPDATA%\Noptic\wyvern-release-key.dpapi, encrypted to this Windows account (DPAPI). It is
# never committed or uploaded. If it is lost, make a new key, add its public half to UpdateSignature.ReleaseKeys and ship
# that build by hand once (older copies keep trusting only the old key).
param([Parameter(Mandatory = $true)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = 'redoninho/WyvernCenter'
$keyFile = Join-Path $env:APPDATA 'Noptic\wyvern-release-key.dpapi'
if (-not (Test-Path $keyFile)) { throw "Release key not found: $keyFile" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("wyvern-sign-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    $version = $Tag.TrimStart('v')
    $zipName = "WyvernCenter-$version.zip"
    gh release download $Tag -R $repo -p $zipName -D $work
    if ($LASTEXITCODE -ne 0) { throw "Couldn't download $zipName from release $Tag." }
    $zip = Join-Path $work $zipName

    # Sign only what release.yml built from this tag at the commit THIS clone has for it (the code you reviewed), never
    # just "anything attested by the repo": a tag moved or pushed by someone else fails here.
    $commit = (git -C $PSScriptRoot rev-parse "$Tag^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $commit) { throw "Tag $Tag isn't in this clone; fetch and review it first." }
    $remote = (git -C $PSScriptRoot ls-remote origin "refs/tags/$Tag^{}" "refs/tags/$Tag") | ForEach-Object { ($_ -split "`t")[0] }
    if ($remote -notcontains $commit) { throw "GitHub's $Tag doesn't point at your local $Tag ($commit); not signing." }
    gh attestation verify $zip -R $repo --signer-workflow "github.com/$repo/.github/workflows/release.yml" `
        --source-ref "refs/tags/$Tag" --source-digest $commit | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "The zip wasn't built by release.yml from $Tag at $commit; not signing it." }

    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -ne 'WyvernCenter.exe') { throw 'The zip should hold only WyvernCenter.exe.' }
        $exe = Join-Path $work 'WyvernCenter.exe'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.Entries[0], $exe)
    } finally { $archive.Dispose() }

    $info = (Get-Item $exe).VersionInfo
    if ("$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" -ne $version) { throw "The exe is version $($info.FileVersion), not $version." }
    $exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()

    # Same bytes as UpdateSignature.Message.
    $message = [Text.Encoding]::ASCII.GetBytes("WyvernCenter release`n$Tag`n$exeHash`n")
    $digest = [Security.Cryptography.SHA256]::Create().ComputeHash($message)

    $blob = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile),
        [Text.Encoding]::ASCII.GetBytes('WyvernCenter release key'), 'CurrentUser')
    $key = [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    [Array]::Clear($blob, 0, $blob.Length)
    $ecdsa = New-Object Security.Cryptography.ECDsaCng($key)
    try {
        $sig = $ecdsa.SignHash($digest)   # r||s, 64 bytes
        if ($sig.Length -ne 64 -or -not $ecdsa.VerifyHash($digest, $sig)) { throw 'Signing failed.' }
    } finally { $ecdsa.Dispose(); $key.Dispose() }

    $sigFile = Join-Path $work 'WyvernCenter.exe.sig'
    [IO.File]::WriteAllText($sigFile, [Convert]::ToBase64String($sig), [Text.Encoding]::ASCII)
    gh release upload $Tag $sigFile -R $repo --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Upload failed.' }
    "Signed $Tag (exe sha256 $exeHash). The updater will offer it from the next check."
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
