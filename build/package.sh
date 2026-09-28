#!/usr/bin/env bash
# Builds the Linux package: tests, self-contained PairSync, tar.gz with install.sh and SHA256SUMS in artifacts/.
# Needs native/runtimes/linux-x64
# (native/build-linux.sh); without it the app falls back to a system libdatachannel, which the package cannot ship.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid=linux-x64
skip_tests=false
[[ "${1:-}" == "--skip-tests" ]] && skip_tests=true

version="$(dotnet msbuild "$root/src/PairSync.Desktop/PairSync.Desktop.csproj" -getProperty:Version | tr -d '[:space:]')"
[[ -n "$version" ]] || { echo "Could not read the version." >&2; exit 1; }
if ! compgen -G "$root/native/runtimes/$rid/native/*.so*" >/dev/null; then
    echo "Native libraries missing in native/runtimes/$rid. Run native/build-linux.sh first." >&2
    exit 1
fi

artifacts="$root/artifacts"
name="PairSync-$version-$rid"
stage="$artifacts/stage/$name"
rm -rf "$stage"
mkdir -p "$stage/app"

if [[ "$skip_tests" == false ]]; then
    dotnet build "$root/PairSync.slnx" -c Release -nologo -v q
    for suite in UnitTests IntegrationTests UiTests; do
        "$root/tests/PairSync.$suite/bin/Release/net10.0/PairSync.$suite"
    done
fi

dotnet publish "$root/src/PairSync.Desktop/PairSync.Desktop.csproj" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=none -o "$stage/app" -nologo -v q
# Native debug symbols of Skia and HarfBuzz come with their packages; users do not need them.
find "$stage/app" -name '*.pdb' -delete
cp "$root/build/linux/install.sh" "$root/build/linux/pairsync.desktop" "$root/build/linux/pairsync.svg" "$stage/"
chmod +x "$stage/install.sh" "$stage/app/PairSync"

tar -C "$artifacts/stage" -czf "$artifacts/$name.tar.gz" "$name"
(cd "$artifacts" && sha256sum PairSync-"$version"-*.tar.gz > SHA256SUMS-linux)
ls -l "$artifacts/$name.tar.gz" "$artifacts/SHA256SUMS-linux"
