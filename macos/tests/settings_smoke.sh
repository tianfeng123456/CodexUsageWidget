#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../.."
mkdir -p macos/.build
# Compile the real delegate into the test runner without its application entry point.
python3 - <<'PY'
from pathlib import Path
Path('macos/.build/AppUnderTest.swift').write_text(Path('macos/Sources/App.swift').read_text().replace('@main\n',''))
PY
xcrun swiftc macos/Sources/Model.swift macos/Sources/Geometry.swift macos/Sources/Views.swift macos/.build/AppUnderTest.swift macos/tests/SettingsTests.swift -o macos/.build/settings-tests -framework AppKit -framework SwiftUI -framework Charts -framework ServiceManagement
macos/.build/settings-tests
