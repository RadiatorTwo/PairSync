# Builds the Windows package: tests, self-contained single-file PairSync.exe, zip and SHA256SUMS in artifacts/.
# Signing is optional: set PAIRSYNC_SIGN_CERT (path to a .pfx) and PAIRSYNC_SIGN_PASSWORD; PAIRSYNC_TIMESTAMP_URL
# overrides the timestamp server. Needs native/runtimes/win-x64 (native/build-win.ps1).
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
$exe = Join-Path $out 'PairSync.exe'
if ($env:PAIRSYNC_SIGN_CERT) {
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -like '*x64*' | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe not found; install the Windows SDK.' }
    $timestamp = if ($env:PAIRSYNC_TIMESTAMP_URL) { $env:PAIRSYNC_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    & $signtool.FullName sign /f $env:PAIRSYNC_SIGN_CERT /p $env:PAIRSYNC_SIGN_PASSWORD /fd SHA256 /tr $timestamp /td SHA256 $exe
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
} else {
    Write-Warning 'PAIRSYNC_SIGN_CERT not set: PairSync.exe is unsigned. Windows SmartScreen will warn on first start.'
}

$name = "PairSync-$version-$rid"
$zip = Join-Path $artifacts "$name.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out/*" -DestinationPath $zip

$sums = Join-Path $artifacts 'SHA256SUMS-win'
Get-ChildItem $artifacts -Filter "PairSync-$version-*.zip" | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content -Encoding ascii $sums

Get-Item $zip, $sums | Select-Object Name, Length
