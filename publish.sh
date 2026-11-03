#!/usr/bin/env bash
set -euo pipefail

# Usage: ./publish.sh [rid] [output-dir]
#   rid         Runtime identifier (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64).
#               Detected from uname when omitted.
#   output-dir  Publish destination, relative to this script's directory.
#               Defaults to artifacts/publish/<rid>.

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  echo "Usage: ./publish.sh [rid] [output-dir]"
  exit 0
fi

cd "$(dirname "$0")"

detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os:$arch" in
    Darwin:arm64)                 echo "osx-arm64" ;;
    Darwin:x86_64)                echo "osx-x64" ;;
    Linux:x86_64)                 echo "linux-x64" ;;
    Linux:aarch64|Linux:arm64)    echo "linux-arm64" ;;
    *)
      echo "Error: unsupported platform '$os $arch'. Pass a runtime identifier explicitly." >&2
      exit 1
      ;;
  esac
}

RID="${1:-}"
if [[ -z "$RID" ]]; then
  RID="$(detect_rid)"
fi

OUTPUT="${2:-artifacts/publish/$RID}"

echo "Publishing AzureWorkbench for $RID to $OUTPUT"
dotnet publish src/AzureWorkbench \
  -c Release \
  -r "$RID" \
  -o "$OUTPUT"

OUTPUT_ABS="$(cd "$OUTPUT" && pwd)"
echo "Output: $OUTPUT_ABS"
EXE="AzureWorkbench"
[[ "$RID" == win-* ]] && EXE="AzureWorkbench.exe"
echo "Executable: $OUTPUT_ABS/$EXE"
