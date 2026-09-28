# Builds the Windows package: tests, then the self-contained single-file PairSync-<version>-win-x64.exe and
# SHA256SUMS-win in artifacts/. Needs native/runtimes/win-x64 (native/build-win.ps1).
# -Version overrides Directory.Build.props (the release workflow passes the tag).
param(
    [switch]$SkipTests,
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rid = 'win-x64'
$version = if ($Version) { $Version } else { (& dotnet msbuild "$root/src/PairSync.Desktop/PairSync.Desktop.csproj" -getProperty:Version).Trim() }
if (-not $version) { throw 'Could not read the version from Directory.Build.props.' }
if (-not (Test-Path "$root/native/runtimes/$rid/native/*.dll")) { throw "Native libraries missing in native/runtimes/$rid. Run native/build-win.ps1 first." }

$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts "publish/$rid"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not $SkipTests) {
    & dotnet build "$root/PairSync.slnx" -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($suite in 'UnitTests', 'IntegrationTests', 'UiTests') {
        & "$root/tests/PairSync.$suite/bin/Release/net10.0/PairSync.$suite.exe"
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $suite" }
    }
}

& dotnet publish "$root/src/PairSync.Desktop/PairSync.Desktop.csproj" -c Release -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=none "-p:Version=$version" -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$exe = Join-Path $artifacts "PairSync-$version-$rid.exe"
Copy-Item (Join-Path $out 'PairSync.exe') $exe -Force

$sums = Join-Path $artifacts 'SHA256SUMS-win'
# .NET directly: Get-FileHash is missing when the Utility module does not load (seen when started from Git Bash).
$sha = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($exe)
try { $hash = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) } finally { $stream.Dispose(); $sha.Dispose() }
# LF line ending, so sha256sum -c also accepts the file on Linux.
[System.IO.File]::WriteAllText($sums, ('{0}  {1}' -f $hash, (Split-Path $exe -Leaf)) + "`n")

Get-Item $exe, $sums | Select-Object Name, Length
