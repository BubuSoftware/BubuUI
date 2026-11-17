#!/bin/bash
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
app="${1:?Usage: verify-macos-package.sh path/to/Gallery.app [osx-arm64|osx-x64]}"
rid="${2:-}"
if [ -z "$rid" ]; then
  if [ "$(uname -m)" = arm64 ]; then rid=osx-arm64; else rid=osx-x64; fi
fi
python3 "$script_dir/bundle_macos.py" --verify "$app" --rid "$rid"
executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")
# Exercise the same native launcher as Finder, without development library overrides.
env -u DYLD_LIBRARY_PATH -u DYLD_FALLBACK_LIBRARY_PATH -u DYLD_FRAMEWORK_PATH \
  "$app/Contents/MacOS/$executable" --frames 5 --always-render
