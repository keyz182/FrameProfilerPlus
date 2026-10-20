#!/bin/sh
# Prints the CHANGELOG.md section of one release. Usage: changelog-section.sh <tag>
set -eu
tag=${1:?usage: changelog-section.sh <tag>}
section=$(awk -v tag="$tag" '
  index($0, "## [" tag "](") == 1 { found = 1; next }
  found && ($0 == "- - -" || index($0, "## [") == 1) { exit }
  found { print }
' CHANGELOG.md)
[ -n "$section" ] || { echo "changelog-section: no section for $tag in CHANGELOG.md" >&2; exit 1; }
printf '%s\n' "$section"
