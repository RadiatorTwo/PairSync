#!/usr/bin/env bash
# Builds libdatachannel (>= 0.24.5, overlay port) with vcpkg and copies the shared libraries
# to native/runtimes/linux-x64/native. Alternative: install the distro package
# (e.g. AUR "libdatachannel" on CachyOS/Arch); the loader falls back to the system library.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
vcpkg="${VCPKG_ROOT:?set VCPKG_ROOT to your vcpkg checkout}/vcpkg"
"$vcpkg" install --triplet x64-linux-dynamic --x-manifest-root="$here" --x-install-root="$here/vcpkg_installed"
out="$here/runtimes/linux-x64/native"
mkdir -p "$out"
cp -L "$here"/vcpkg_installed/x64-linux-dynamic/lib/*.so* "$out"/
ls -l "$out"
