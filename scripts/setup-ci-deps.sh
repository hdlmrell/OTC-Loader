#!/usr/bin/env bash
# Collects MelonLoader reference DLLs from your local r2modman profile and
# uploads them as a "deps" pre-release for CI builds.
#
# Prerequisites: gh CLI authenticated (gh auth login)
# Usage: bash scripts/setup-ci-deps.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGETS="$REPO_ROOT/OTC-Loader/LocalPaths.targets"

if ! command -v gh &>/dev/null; then
  echo "Error: gh CLI not found. Install from https://cli.github.com"
  exit 1
fi

if [ ! -f "$TARGETS" ]; then
  echo "Error: OTC-Loader/LocalPaths.targets not found."
  echo "Create it with your local MelonLoader path first."
  exit 1
fi

extract_tag() {
  sed -n "s/.*<$1>\(.*\)<\/$1>.*/\1/p" "$TARGETS" | tr -d '\r'
}

# Resolve MSBuild-style $(VarName) references against other tags in the file
resolve() {
  local val
  val=$(extract_tag "$1")
  while [[ "$val" == *'$('* ]]; do
    local ref
    ref=$(echo "$val" | sed -n 's/.*\$(\([^)]*\)).*/\1/p')
    local ref_val
    ref_val=$(extract_tag "$ref")
    val="${val//\$($ref)/$ref_val}"
  done
  echo "$val"
}

ML_NET6_DIR=$(resolve MelonLoaderNet6Path)

if [ -z "$ML_NET6_DIR" ]; then
  echo "Error: MelonLoaderNet6Path not found in LocalPaths.targets"
  exit 1
fi

if [ ! -d "$ML_NET6_DIR" ]; then
  echo "Error: MelonLoader directory does not exist: $ML_NET6_DIR"
  exit 1
fi

STAGING=$(mktemp -d)
trap 'rm -rf "$STAGING"' EXIT

mkdir -p "$STAGING/melonloader/net6"

# OTC Loader only needs these 3 DLLs to compile
DLLS=(MelonLoader.dll Mono.Cecil.dll Newtonsoft.Json.dll)

echo "Collecting MelonLoader DLLs from: $ML_NET6_DIR"
for dll in "${DLLS[@]}"; do
  if [ -f "$ML_NET6_DIR/$dll" ]; then
    cp "$ML_NET6_DIR/$dll" "$STAGING/melonloader/net6/"
    echo "  $dll"
  else
    echo "  Error: $dll not found!"
    exit 1
  fi
done

ARCHIVE="$REPO_ROOT/deps.tar.gz"
(cd "$STAGING" && tar -czf "$ARCHIVE" .)

echo ""
echo "Packed ${#DLLS[@]} DLLs"
echo "Archive: $(du -h "$ARCHIVE" | cut -f1)"
echo ""

cd "$REPO_ROOT"
if gh release view deps &>/dev/null; then
  echo "Updating existing deps release..."
  gh release upload deps "$ARCHIVE" --clobber
else
  echo "Creating deps pre-release..."
  gh release create deps "$ARCHIVE" \
    --prerelease \
    --title "Build Dependencies" \
    --notes "MelonLoader DLLs for CI builds. Do not delete this release."
fi

rm -f "$ARCHIVE"
echo "Done!"
