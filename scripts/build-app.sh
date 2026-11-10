#!/bin/bash
# Builds build/Ollameter.app from the Swift package.
# Usage: scripts/build-app.sh [VERSION] [BUILD_NUMBER]
set -euo pipefail
cd "$(dirname "$0")/.."

version=${1:-}
build_number=${2:-}

swift build -c release
binary="$(swift build -c release --show-bin-path)/Ollameter"

app=build/Ollameter.app
mkdir -p "$app/Contents/MacOS"
# A new file rather than an overwrite, so a copy of the app that's running keeps working.
cp "$binary" "$app/Contents/MacOS/Ollameter.new"
mv -f "$app/Contents/MacOS/Ollameter.new" "$app/Contents/MacOS/Ollameter"
cp Resources/Info.plist "$app/Contents/Info.plist"
if [[ -n "$version" ]]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $version" "$app/Contents/Info.plist"
fi
if [[ -n "$build_number" ]]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $build_number" "$app/Contents/Info.plist"
fi
codesign --force --sign - "$app"
echo "Built $PWD/$app"
