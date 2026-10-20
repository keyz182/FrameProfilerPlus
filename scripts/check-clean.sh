#!/bin/sh
# Stops a release when the working tree has uncommitted or untracked files. Source that is not committed
# would still be compiled into the DLL. cog writes CHANGELOG.md before it runs the hooks, so that one file
# is allowed.
set -eu
dirty=$(git status --porcelain | cut -c4- | grep -v -x 'CHANGELOG.md' || true)
if [ -n "$dirty" ]; then
  echo "The working tree is not clean. Commit or remove these changes first:" >&2
  echo "$dirty" >&2
  exit 1
fi
