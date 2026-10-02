#!/usr/bin/env bash
# Starts an AppImage with an empty profile and waits for the window to be ready: the log's "Shell ready" line.
# With no display it runs under xvfb-run. Nothing outside a temporary folder is read or written.
#
# Usage: packaging/linux/smoke-test.sh <XXSM-…-x86_64.AppImage> [seconds to wait, default 60]
set -euo pipefail

appimage="$(readlink -f "${1:?usage: packaging/linux/smoke-test.sh <AppImage> [seconds]}")"
wait_seconds="${2:-60}"

profile="$(mktemp -d)"
trap 'kill "$pid" 2>/dev/null || true; rm -rf "$profile"' EXIT

export XXSM_CONFIG_HOME="$profile/config" XXSM_DATA_HOME="$profile/data"
export XXSM_CACHE_HOME="$profile/cache" XXSM_STATE_HOME="$profile/state"
# Unpacked rather than mounted: containers have no FUSE.
export APPIMAGE_EXTRACT_AND_RUN=1

chmod +x "$appimage"
if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
  "$appimage" > "$profile/output.txt" 2>&1 &
else
  xvfb-run --auto-servernum "$appimage" > "$profile/output.txt" 2>&1 &
fi
pid=$!

for ((second = 0; second < wait_seconds; second++)); do
  if grep -qs "Shell ready" "$profile"/state/logs/*.log; then
    grep -h "Shell ready" "$profile"/state/logs/*.log
    exit 0
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    break
  fi
  sleep 1
done

echo "The window did not get ready within $wait_seconds seconds." >&2
echo "--- output" >&2
cat "$profile/output.txt" >&2 || true
echo "--- log" >&2
cat "$profile"/state/logs/*.log >&2 2>/dev/null || echo "(no log)" >&2
exit 1
