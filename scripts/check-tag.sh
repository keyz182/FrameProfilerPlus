#!/bin/sh
# Release checks that need no build. Usage: check-tag.sh <tag> [<commit-ish>]  (default HEAD)
set -eu
tag=${1:?usage: check-tag.sh <tag> [<commit-ish>]}
ref=${2:-HEAD}
fail() { echo "check-tag: $1" >&2; exit 1; }

case "$tag" in
  v[0-9]*.[0-9]*.[0-9]*) ;;
  *) fail "the tag $tag is not of the form vMAJOR.MINOR.PATCH" ;;
esac
version=${tag#v}
grep -q "<modVersion>$version</modVersion>" About/About.xml || fail "About.xml modVersion is not $version"
[ -f LICENSE ] || fail "LICENSE is missing"
subject=$(git log -1 --format=%s "$ref")
[ "$subject" = "chore(version): $tag" ] || fail "the tagged commit is '$subject', not 'chore(version): $tag'"
echo "check-tag: $tag ok"
