#!/bin/sh
# Checks a release before cog commits it. Usage: verify-release.sh <version>
# SHIPPED_DLL, RELEASE_DLL and VERIFY_GIT=0 exist so scripts/test-verify-release.sh can run the check on its own.
set -eu
version=${1:?usage: verify-release.sh <version>}
root=$(git rev-parse --show-toplevel)
shipped=${SHIPPED_DLL:-$root/1.7/Assemblies/FrameProfilerPlus.dll}
fail() { echo "verify-release: $1" >&2; exit 1; }

# The compiler output of the Release build; the project does not add a framework folder under obj.
built=${RELEASE_DLL:-$root/Source/obj/Release/FrameProfilerPlus.dll}
[ -f "$built" ] || fail "no Release build output at $built"
[ -f "$shipped" ] || fail "no DLL to ship at $shipped"
cmp -s "$shipped" "$built" || fail "the DLL to ship is not the Release build output ($built)"

grep -q "<modVersion>$version</modVersion>" "$root/About/About.xml" || fail "About.xml modVersion is not $version"
[ -f "$root/LICENSE" ] || fail "LICENSE is missing"

if [ "${VERIFY_GIT:-1}" != "0" ]; then
  unexpected=$(git -C "$root" status --porcelain | cut -c4- | grep -v -x -e '1.7/Assemblies/FrameProfilerPlus.dll' -e 'About/About.xml' -e 'CHANGELOG.md' || true)
  [ -z "$unexpected" ] || fail "unexpected changes in the working tree: $unexpected"
fi
echo "verify-release: $version ok"
