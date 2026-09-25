# Builds libdatachannel (>= 0.24.5, overlay port) with vcpkg in manifest mode and copies
# the runtime DLLs to native/runtimes/win-x64/native, where the .NET projects pick them up.
param(
    [string]$Vcpkg = $env:VCPKG_ROOT
)
$ErrorActionPreference = 'Stop'
if (-not $Vcpkg) { $Vcpkg = 'E:\!Code2\vcpkg' }
$exe = Join-Path $Vcpkg 'vcpkg.exe'
if (-not (Test-Path $exe)) { throw "vcpkg.exe not found under '$Vcpkg'. Pass -Vcpkg or set VCPKG_ROOT." }

# vcvarsall finds the Universal CRT only through the registry value KitsRoot10. If an SDK
# install lost that value, pre-seed LIB/INCLUDE with the UCRT paths of the newest SDK.
$kitsKey = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots'
$kitsRoot = (Get-ItemProperty -Path $kitsKey -Name KitsRoot10 -ErrorAction SilentlyContinue).KitsRoot10
if (-not $kitsRoot) {
    $kits = 'C:\Program Files (x86)\Windows Kits\10'
    $ucrt = Get-ChildItem (Join-Path $kits 'Lib') -Directory |
        Where-Object { Test-Path (Join-Path $_.FullName 'ucrt\x64\ucrt.lib') } |
        Sort-Object { [version]$_.Name } | Select-Object -Last 1
    if (-not $ucrt) { throw 'No Universal CRT found under Windows Kits\10. Repair the Windows SDK.' }
    Write-Warning "KitsRoot10 missing in the registry; using UCRT $($ucrt.Name) directly. Repairing the Windows SDK fixes this permanently."
    $env:LIB = Join-Path $kits "Lib\$($ucrt.Name)\ucrt\x64"
    $env:INCLUDE = Join-Path $kits "Include\$($ucrt.Name)\ucrt"
}

$here = $PSScriptRoot
$installRoot = Join-Path $here 'vcpkg_installed'
& $exe install --triplet x64-windows --x-manifest-root=$here --x-install-root=$installRoot
if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed ($LASTEXITCODE)" }

$bin = Join-Path $installRoot 'x64-windows\bin'
$out = Join-Path $here 'runtimes\win-x64\native'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Get-ChildItem $bin -Filter *.dll | Where-Object Name -ne 'legacy.dll' | Copy-Item -Destination $out -Force
Get-ChildItem $out | Select-Object Name, Length
