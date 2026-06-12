#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

die() {
  echo "Version sync check failed: $*" >&2
  exit 1
}

open_files_in_vscode() {
  if command -v code >/dev/null 2>&1; then
    code --reuse-window \
      "$ROOT_DIR/SledHeadless/Directory.Build.props" \
      "$ROOT_DIR/thunderstore.toml" \
      "$ROOT_DIR/CHANGELOG.md" >/dev/null 2>&1 || true
  fi
}

require_file() {
  local path="$1"
  [ -f "$path" ] || die "missing required file: $path"
}

require_file "SledHeadless/Directory.Build.props"
require_file "thunderstore.toml"
require_file "CHANGELOG.md"

props_version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' SledHeadless/Directory.Build.props | head -n 1)"
thunder_version="$(sed -n 's/^[[:space:]]*versionNumber[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' thunderstore.toml | head -n 1)"
changelog_version="$(sed -n 's/^## \[\([^]]\+\)\].*/\1/p' CHANGELOG.md | head -n 1)"

[ -n "$props_version" ] || die "could not parse <Version> from SledHeadless/Directory.Build.props"
[ -n "$thunder_version" ] || die "could not parse versionNumber from thunderstore.toml"
[ -n "$changelog_version" ] || die "could not parse latest changelog heading from CHANGELOG.md"

if [ "$props_version" != "$thunder_version" ] || [ "$props_version" != "$changelog_version" ]; then
  {
    echo "Version values must match:"
    echo "  SledHeadless/Directory.Build.props : $props_version"
    echo "  thunderstore.toml                  : $thunder_version"
    echo "  CHANGELOG.md (latest heading)      : $changelog_version"
    echo
    echo "Update these files so they all use the same version before committing."
  } >&2
  open_files_in_vscode
  exit 1
fi

echo "Version sync check passed: $props_version"
