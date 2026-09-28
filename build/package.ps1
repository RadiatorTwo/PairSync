# Builds the Windows package: tests, self-contained single-file PairSync.exe, zip and SHA256SUMS in artifacts/.
# Needs native/runtimes/win-x64 (native/build-win.ps1).
param(
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$rid = 'win-x64'
$version = (& dotnet msbuild "$root/src/PairSync.Desktop/PairSync.Desktop.csproj" -getProperty:Version).Trim()
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
    -p:PublishSingleFile=true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# Native debug symbols of Skia and HarfBuzz come with their packages; users do not need them.
Get-ChildItem $out -Filter *.pdb | Remove-Item -Force
$name = "PairSync-$version-$rid"
$zip = Join-Path $artifacts "$name.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out/*" -DestinationPath $zip

$sums = Join-Path $artifacts 'SHA256SUMS-win'
# .NET directly: Get-FileHash is missing when the Utility module does not load (seen when started from Git Bash).
$sha = [System.Security.Cryptography.SHA256]::Create()
Get-ChildItem $artifacts -Filter "PairSync-$version-*.zip" | ForEach-Object {
    $stream = [System.IO.File]::OpenRead($_.FullName)
    try { $hash = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) } finally { $stream.Dispose() }
    '{0}  {1}' -f $hash, $_.Name
} | Set-Content -Encoding ascii $sums
$sha.Dispose()

Get-Item $zip, $sums | Select-Object Name, Length
