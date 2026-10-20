#!/bin/sh
# Proves that verify-release.sh fails on a Debug DLL and passes on the Release one.
set -eu
root=$(git rev-parse --show-toplevel)
version=$(sed -n 's#.*<modVersion>\(.*\)</modVersion>.*#\1#p' "$root/About/About.xml")
tmp=$(mktemp -d)
trap 'rm -r -- "$tmp"' EXIT

dotnet build -c Debug --no-incremental "$root/Source/FrameProfilerPlus.csproj" -p:OutDir="$tmp/debug/" > /dev/null
dotnet build -c Release --no-incremental "$root/Source/FrameProfilerPlus.csproj" -p:OutDir="$tmp/release/" > /dev/null

if VERIFY_GIT=0 SHIPPED_DLL="$tmp/debug/FrameProfilerPlus.dll" sh "$root/scripts/verify-release.sh" "$version" > /dev/null 2>&1; then
  echo "FAIL: a Debug DLL passed verify-release.sh" >&2
  exit 1
fi
VERIFY_GIT=0 SHIPPED_DLL="$tmp/release/FrameProfilerPlus.dll" sh "$root/scripts/verify-release.sh" "$version" > /dev/null
echo "ok: Debug DLL rejected, Release DLL accepted"
