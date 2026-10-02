#!/usr/bin/env bash
# Draws the application's icons from packaging/xxsm.svg, in two versions:
#   inside the app — the window's icon (src/Xxsm.Desktop/Assets/xxsm.ico): the blue background, corners rounded;
#   outside it — the Windows exe's file icon (src/Xxsm.Desktop/app.ico) and the Linux menu and dock icon
#   (packaging/linux/xxsm.png): the X alone, on transparency.
# Both come from the SVG's background square: rounded for one, taken out for the other.
# Needs rsvg-convert (librsvg) and ImageMagick 7. Run it after changing the SVG, and commit what it writes.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
svg="$root/packaging/xxsm.svg"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

background='<rect width="1254" height="1254" fill="#0197fd"/>'
if ! grep -qF "$background" "$svg"; then
  echo "packaging/xxsm.svg has no background square this script knows: $background" >&2
  exit 1
fi

# A fifth of the side, the rounding most desktops give their own icons.
sed "s|$background|<rect width=\"1254\" height=\"1254\" rx=\"250\" ry=\"250\" fill=\"#0197fd\"/>|" "$svg" > "$scratch/inside.svg"
sed "s|$background||" "$svg" > "$scratch/outside.svg"

# Every size of one version into an .ico.
ico() {
  local version="$1" target="$2" pngs=()
  for size in 16 20 24 32 40 48 64 128 256; do
    rsvg-convert -w "$size" -h "$size" "$scratch/$version.svg" -o "$scratch/$version-$size.png"
    pngs+=("$scratch/$version-$size.png")
  done
  magick "${pngs[@]}" "$target"
}

ico inside "$root/src/Xxsm.Desktop/Assets/xxsm.ico"
ico outside "$root/src/Xxsm.Desktop/app.ico"
cp "$scratch/outside-256.png" "$root/packaging/linux/xxsm.png"
echo "Wrote src/Xxsm.Desktop/Assets/xxsm.ico, src/Xxsm.Desktop/app.ico and packaging/linux/xxsm.png"
