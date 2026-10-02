#!/usr/bin/env bash
# Builds XXSM-<version>-x86_64.AppImage: the window, self-contained for linux-x64, one file that runs on any
# 64-bit Linux desktop. appimagetool and its runtime are downloaded once, pinned by checksum.
#
# Usage: packaging/linux/build-appimage.sh <version> [output folder, default artifacts/]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
version="${1:?usage: packaging/linux/build-appimage.sh <version> [output folder]}"
out="$(mkdir -p "${2:-$root/artifacts}" && cd "${2:-$root/artifacts}" && pwd)"

tool_url=https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage
tool_sha=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0
runtime_url=https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64
runtime_sha=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d

cache="${XDG_CACHE_HOME:-$HOME/.cache}/xxsm-packaging"
mkdir -p "$cache"

# Downloads a file into the cache unless it is there with the right checksum; prints its path.
fetch() {
  local url="$1" sha="$2" file="$cache/$(basename "$1")"
  if [[ ! -f "$file" ]] || ! sha256sum --check --status <<<"$sha  $file"; then
    curl --fail --location --silent --show-error --output "$file.part" "$url"
    if ! sha256sum --check --status <<<"$sha  $file.part"; then
      echo "$url does not have the expected checksum; not used." >&2
      rm -f "$file.part"
      exit 1
    fi
    mv "$file.part" "$file"
  fi
  chmod +x "$file"
  echo "$file"
}

tool="$(fetch "$tool_url" "$tool_sha")"
runtime="$(fetch "$runtime_url" "$runtime_sha")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
appdir="$work/XXSM.AppDir"

dotnet publish "$root/src/Xxsm.Desktop" -c Release -r linux-x64 --self-contained --nologo \
  -p:Version="$version" -o "$appdir/usr/lib/xxsm"

install -m 755 "$root/packaging/linux/AppRun" "$appdir/AppRun"
install -m 644 "$root/packaging/linux/xxsm.desktop" "$appdir/xxsm.desktop"
install -m 644 "$root/packaging/linux/xxsm.png" "$appdir/xxsm.png"
ln -s xxsm.png "$appdir/.DirIcon"
install -D -m 644 -t "$appdir/usr/share/doc/xxsm" "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md"

target="$out/XXSM-$version-x86_64.AppImage"
# Extract-and-run: CI machines and containers often have no FUSE to mount the tool's own AppImage.
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream --runtime-file "$runtime" "$appdir" "$target"
echo "Built $target"
