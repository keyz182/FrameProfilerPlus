#!/bin/sh
# Checks that a release zip holds exactly the mod files. Usage: check-zip.sh <zip>
set -eu
zip=${1:?usage: check-zip.sh <zip>}
expected='FrameProfilerPlus/
FrameProfilerPlus/1.7/
FrameProfilerPlus/1.7/Assemblies/
FrameProfilerPlus/1.7/Assemblies/FrameProfilerPlus.dll
FrameProfilerPlus/About/
FrameProfilerPlus/About/About.xml
FrameProfilerPlus/LICENSE'
actual=$(unzip -Z1 "$zip" | sort)
if [ "$actual" != "$expected" ]; then
  echo "check-zip: unexpected zip contents:" >&2
  echo "$actual" >&2
  exit 1
fi
echo "check-zip: ok"
