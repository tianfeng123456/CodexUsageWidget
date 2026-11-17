#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET_BIN="${DOTNET_BIN:-$(command -v dotnet || true)}"
if [ -z "$DOTNET_BIN" ] && [ -x ../toolchain/dotnet/dotnet ]; then DOTNET_BIN="$PWD/../toolchain/dotnet/dotnet"; fi
if [ -z "$DOTNET_BIN" ]; then echo 'Install .NET 8 SDK or set DOTNET_BIN.' >&2; exit 1; fi
ARCH="${ARCH:-arm64}"
APP="$PWD/dist/Codex Usage Widget.app"
"$DOTNET_BIN" clean src/CodexUsageWidget.Bridge -c Release -r "osx-$ARCH" >/dev/null
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Helpers" "$APP/Contents/Resources"
"$DOTNET_BIN" publish src/CodexUsageWidget.Bridge -c Release -r "osx-$ARCH" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$APP/Contents/Helpers"
xcrun swiftc -O -target "$ARCH-apple-macosx13.0" macos/Sources/*.swift -o "$APP/Contents/MacOS/CodexUsageWidget" -framework AppKit -framework SwiftUI -framework Charts -framework ServiceManagement
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.tianfeng.codexusagewidget</string>
<key>CFBundleName</key><string>Codex Usage Widget</string>
<key>CFBundleDisplayName</key><string>Codex 用量面板</string>
<key>CFBundleExecutable</key><string>CodexUsageWidget</string>
<key>CFBundleShortVersionString</key><string>1.3.0</string>
<key>CFBundleVersion</key><string>6</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>LSMinimumSystemVersion</key><string>13.0</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
<key>CFBundleIconFile</key><string>AppIcon</string>
</dict></plist>
PLIST
cp LICENSE "$APP/Contents/Resources/LICENSE"
if [ -f macos/AppIcon.icns ]; then cp macos/AppIcon.icns "$APP/Contents/Resources/"; fi
find "$APP/Contents/Helpers" -name "*.pdb" -delete
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
python3 macos/tests/bridge_smoke.py "$APP/Contents/Helpers/CodexUsageWidget.Bridge"
echo "$APP"
