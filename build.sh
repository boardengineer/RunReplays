#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
GAME_DIR="${STS2_GAME_DIR:-C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2}"
MODS_DIR="${STS2_MODS_DIR:-$GAME_DIR/mods}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$SCRIPT_DIR/RunReplays/RunReplays.csproj"
OUT_DIR="$SCRIPT_DIR/out/RunReplays"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: 'dotnet' not found. Install the .NET 9 SDK:" >&2
  echo "  https://dotnet.microsoft.com/download/dotnet/9.0" >&2
  exit 1
fi

if [[ ! -f "$GAME_DIR/data_sts2_windows_x86_64/sts2.dll" ]]; then
  echo "ERROR: Could not find sts2.dll under:" >&2
  echo "  $GAME_DIR/data_sts2_windows_x86_64" >&2
  echo "Set STS2_GAME_DIR to your Slay the Spire 2 install path if it differs." >&2
  exit 1
fi

echo "=== Building RunReplays ($CONFIGURATION) ==="
echo "Game directory : $GAME_DIR"
echo "Output         : $OUT_DIR"
echo "Mods directory : $MODS_DIR"
echo

dotnet build "$PROJECT" -c "$CONFIGURATION" -o "$OUT_DIR" -p:STS2GameDir="$GAME_DIR"

mkdir -p "$MODS_DIR"
cp "$OUT_DIR/RunReplays.dll" "$MODS_DIR/RunReplays.dll"
cp "$SCRIPT_DIR/RunReplays.json" "$MODS_DIR/RunReplays.json"

echo
echo "=== Installed RunReplays ==="
echo "  $MODS_DIR/RunReplays.dll"
echo "  $MODS_DIR/RunReplays.json"
