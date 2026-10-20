#!/bin/sh
# Builds the release zip from the committed tree. It never compiles. Usage: package.sh <version>
set -eu
version=${1:?usage: package.sh <version>}
root=$(git rev-parse --show-toplevel)
out=${OUT_DIR:-$root/dist}
name=FrameProfilerPlus
stage=$(mktemp -d)
trap 'rm -r -- "$stage"' EXIT

mkdir -p "$stage/$name/1.7/Assemblies" "$out"
cp -r "$root/About" "$stage/$name/About"
cp "$root/1.7/Assemblies/FrameProfilerPlus.dll" "$stage/$name/1.7/Assemblies/"
cp "$root/LICENSE" "$stage/$name/"
(cd "$stage" && zip -q -r "$out/$name-v$version.zip" "$name")
echo "$out/$name-v$version.zip"
