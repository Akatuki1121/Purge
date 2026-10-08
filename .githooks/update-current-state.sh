#!/bin/sh
# Calls scripts/Update-CurrentState.ps1 (refreshes docs/CURRENT_STATE.md).
# Never blocks or fails the git operation: runs in the background, always exits 0.
root="$(git rev-parse --show-toplevel 2>/dev/null)" || exit 0
script="$root/scripts/Update-CurrentState.ps1"
[ -f "$script" ] || exit 0
win="$(cygpath -w "$script" 2>/dev/null || echo "$script")"
( powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$win" >/dev/null 2>&1 & )
exit 0
