#!/usr/bin/env bash
# Builds XXSM-<version>-x64.exe: the window, self-contained for win-x64, as one file. Runs on Linux or Windows
# (Git Bash); nothing but the .NET SDK is needed.
#
# Usage: packaging/windows/build-exe.sh <version> [output folder, default artifacts/]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="${1:?usage: packaging/windows/build-exe.sh <version> [output folder]}"
out="$(mkdir -p "${2:-$root/artifacts}" && cd "${2:-$root/artifacts}" && pwd)"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# One file: the native libraries go inside it and are unpacked on first start, and the whole is compressed.
dotnet publish "$root/src/Xxsm.Desktop" -c Release -r win-x64 --self-contained --nologo \
  -p:Version="$version" \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o "$work/publish"

# Only the exe is shipped; the native libraries' debug symbols beside it are not needed to run.
shopt -s nullglob
extra=()
for file in "$work/publish"/*; do
  [[ "$file" == *.pdb ]] || extra+=("$file")
done
if [[ ${#extra[@]} -ne 1 ]]; then
  echo "The publish made more than the exe, so it would not run as one file:" >&2
  printf '  %s\n' "${extra[@]##*/}" >&2
  exit 1
fi

target="$out/XXSM-$version-x64.exe"
cp "$work/publish/Xxsm.Desktop.exe" "$target"
echo "Built $target"
