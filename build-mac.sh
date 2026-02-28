#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# build-mac.sh — Build PST Explorer as a macOS .app bundle
#
# Prerequisites:
#   .NET 8 SDK  — brew install --cask dotnet-sdk  (or dotnet-install script)
#   Xcode CLT   — xcode-select --install          (for swiftc)
#
# Usage:
#   ./build-mac.sh
#
# Output:
#   ./dist/PST Explorer.app
# ─────────────────────────────────────────────────────────────────────────────
export PATH="$HOME/.dotnet:$PATH"
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$SCRIPT_DIR/src"
APP_NAME="PST Explorer"
DIST_DIR="$SCRIPT_DIR/dist"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"

# Detect architecture
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
elif [ "$ARCH" = "x86_64" ]; then
    RID="osx-x64"
else
    echo "Unsupported architecture: $ARCH"
    exit 1
fi

echo "══════════════════════════════════════════════════════════════"
echo "  Building PST Explorer for macOS ($RID)"
echo "══════════════════════════════════════════════════════════════"
echo ""

# ── Step 1: Check prerequisites ───────────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET 8 SDK not found."
    echo ""
    echo "Install it with:"
    echo "  brew install --cask dotnet-sdk"
    echo ""
    echo "Or download from: https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
fi

if ! command -v swiftc &>/dev/null; then
    echo "ERROR: Swift compiler not found."
    echo ""
    echo "Install Xcode Command Line Tools:"
    echo "  xcode-select --install"
    exit 1
fi

DOTNET_VER=$(dotnet --version)
echo "Using .NET SDK: $DOTNET_VER"
echo "Using swiftc:   $(swiftc --version 2>&1 | head -1)"
echo "Target runtime: $RID"
echo ""

# ── Step 2: Restore & Publish .NET app ────────────────────────────────────────
echo "▸ Restoring NuGet packages…"
dotnet restore "$SRC_DIR/PstWeb.csproj" --runtime "$RID" -v quiet

echo "▸ Publishing self-contained app…"
dotnet publish "$SRC_DIR/PstWeb.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    -o "$SCRIPT_DIR/publish" \
    -v quiet

echo "  Published to $SCRIPT_DIR/publish"
echo ""

# ── Step 3: Compile native Swift launcher ─────────────────────────────────────
echo "▸ Compiling native launcher…"
swiftc \
    -O \
    -target "$(uname -m)-apple-macosx12.0" \
    -o "$SCRIPT_DIR/publish/PSTExplorerLauncher" \
    "$SCRIPT_DIR/macos-app/PSTExplorerLauncher.swift"

echo "  Compiled PSTExplorerLauncher"
echo ""

# ── Step 4: Assemble .app bundle ──────────────────────────────────────────────
echo "▸ Assembling $APP_NAME.app …"

rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources/app"

# Copy Info.plist
cp "$SCRIPT_DIR/macos-app/Info.plist" "$APP_BUNDLE/Contents/"

# Copy native launcher into MacOS/
cp "$SCRIPT_DIR/publish/PSTExplorerLauncher" "$APP_BUNDLE/Contents/MacOS/PSTExplorerLauncher"
chmod +x "$APP_BUNDLE/Contents/MacOS/PSTExplorerLauncher"

# Copy published .NET app into Resources/app/
cp -R "$SCRIPT_DIR/publish/"* "$APP_BUNDLE/Contents/Resources/app/"
rm -f "$APP_BUNDLE/Contents/Resources/app/PSTExplorerLauncher"   # don't duplicate the launcher
chmod +x "$APP_BUNDLE/Contents/Resources/app/PstWeb"

# Generate app icon
echo "▸ Generating app icon…"
ICONSET_DIR=$(mktemp -d)/AppIcon.iconset
mkdir -p "$ICONSET_DIR"
SVG_FILE=$(mktemp).svg

cat << 'SVGEOF' > "$SVG_FILE"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">
  <defs>
    <!-- Base App Icon Gradient (Deep Blue/Purple like macOS Mail/Folder) -->
    <linearGradient id="bgGrad" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#4FB0FF"/>
      <stop offset="100%" stop-color="#0062D2"/>
    </linearGradient>
    
    <!-- Box / Folder Back -->
    <linearGradient id="folderBack" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#8BB9EE"/>
      <stop offset="100%" stop-color="#347CC7"/>
    </linearGradient>

    <!-- Envelope -->
    <linearGradient id="envBg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#FFFFFF"/>
      <stop offset="100%" stop-color="#E2E8F0"/>
    </linearGradient>
    <linearGradient id="envFlap" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#F8FAFC"/>
      <stop offset="100%" stop-color="#CBD5E1"/>
    </linearGradient>

    <!-- Folder Front Gradient -->
    <linearGradient id="folderFront" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#73ADEE"/>
      <stop offset="100%" stop-color="#004A9E"/>
    </linearGradient>

    <!-- Glass Frame Gradient -->
    <linearGradient id="glassFrame" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0%" stop-color="#F1F5F9"/>
      <stop offset="100%" stop-color="#64748B"/>
    </linearGradient>
    
    <!-- Glass Lens -->
    <radialGradient id="glassLens" cx="0.4" cy="0.4" r="0.6">
      <stop offset="0%" stop-color="#FFFFFF" stop-opacity="0.9"/>
      <stop offset="30%" stop-color="#A5F3FC" stop-opacity="0.3"/>
      <stop offset="100%" stop-color="#0284C7" stop-opacity="0.1"/>
    </radialGradient>

    <!-- Shadows -->
    <filter id="dropShadowBase" x="-10%" y="-10%" width="120%" height="120%">
      <feDropShadow dx="0" dy="25" stdDeviation="20" flood-color="#000000" flood-opacity="0.3"/>
    </filter>
    <filter id="dropShadowEnv" x="-20%" y="-20%" width="140%" height="140%">
      <feDropShadow dx="0" dy="15" stdDeviation="15" flood-color="#000000" flood-opacity="0.25"/>
    </filter>
    <filter id="dropShadowGlass" x="-30%" y="-30%" width="160%" height="160%">
      <feDropShadow dx="15" dy="25" stdDeviation="15" flood-color="#000000" flood-opacity="0.4"/>
    </filter>
  </defs>

  <!-- Base Squircle -->
  <rect width="1024" height="1024" rx="230" fill="url(#bgGrad)"/>
  
  <!-- Add an inner stroke/highlight to the squircle -->
  <rect width="1024" height="1024" rx="230" fill="none" stroke="#FFFFFF" stroke-opacity="0.4" stroke-width="4"/>

  <!-- Folder Back -->
  <path d="M 150 350 L 400 350 L 450 420 L 874 420 L 874 800 L 150 800 Z" fill="url(#folderBack)"/>
  
  <!-- Envelopes sticking out -->
  <!-- Envelope 1 (Back, slightly tilted) -->
  <g transform="translate(480, 200) rotate(15)" filter="url(#dropShadowEnv)">
    <rect width="400" height="260" rx="20" fill="url(#envBg)"/>
    <path d="M 0 20 Q 0 0 20 0 L 380 0 Q 400 0 400 20 L 220 160 Q 200 175 180 160 Z" fill="url(#envFlap)"/>
    <path d="M 0 240 Q 0 260 20 260 L 380 260 Q 400 260 400 240 L 220 140 Q 200 125 180 140 Z" fill="#FFFFFF" opacity="0.9"/>
  </g>

  <!-- Envelope 2 (Front, straight) -->
  <g transform="translate(200, 220)" filter="url(#dropShadowEnv)">
    <rect width="460" height="300" rx="20" fill="url(#envBg)"/>
    <path d="M 0 20 Q 0 0 20 0 L 440 0 Q 460 0 460 20 L 250 170 Q 230 185 210 170 Z" fill="url(#envFlap)"/>
    <path d="M 0 280 Q 0 300 20 300 L 440 300 Q 460 300 460 280 L 250 150 Q 230 135 210 150 Z" fill="#FFFFFF" opacity="0.9"/>
  </g>

  <!-- Folder Front -->
  <path d="M 120 480 L 400 480 L 450 540 L 904 540 L 854 850 L 170 850 Z" fill="url(#folderFront)" filter="url(#dropShadowBase)"/>
  
  <!-- Magnifying Glass -->
  <g transform="translate(560, 520)" filter="url(#dropShadowGlass)">
    <!-- Handle -->
    <rect x="140" y="140" width="40" height="180" rx="20" transform="rotate(-45 160 215)" fill="url(#glassFrame)"/>
    <!-- Handle bottom -->
    <rect x="140" y="270" width="40" height="40" rx="10" transform="rotate(-45 160 215)" fill="#334155"/>
    
    <!-- Frame -->
    <circle cx="100" cy="100" r="100" fill="none" stroke="url(#glassFrame)" stroke-width="24"/>
    <circle cx="100" cy="100" r="88" fill="none" stroke="#F8FAFC" stroke-width="4"/>
    
    <!-- Lens -->
    <circle cx="100" cy="100" r="88" fill="url(#glassLens)"/>
    
    <!-- Reflection/Highlight -->
    <path d="M 30 100 A 70 70 0 0 1 100 30 A 80 80 0 0 0 50 100 Z" fill="#FFFFFF" opacity="0.7"/>
  </g>
</svg>
SVGEOF

# Render SVG to high-res PNG
sips -s format png "$SVG_FILE" --out "$ICONSET_DIR/base.png" > /dev/null

# Generate all required icon sizes using sips
sizes=(16 32 64 128 256 512)
for s in "${sizes[@]}"; do
    sips -z $s $s "$ICONSET_DIR/base.png" --out "$ICONSET_DIR/icon_${s}x${s}.png" > /dev/null
    s2=$((s * 2))
    sips -z $s2 $s2 "$ICONSET_DIR/base.png" --out "$ICONSET_DIR/icon_${s}x${s}@2x.png" > /dev/null
done
rm "$ICONSET_DIR/base.png"
rm "$SVG_FILE"

if command -v iconutil &>/dev/null; then
    iconutil -c icns "$ICONSET_DIR" -o "$APP_BUNDLE/Contents/Resources/AppIcon.icns" 2>/dev/null || true
fi

echo "▸ Code signing…"
# Sign the .NET runtime components first (deep = false, so we control order)
# Then sign the outer bundle with --identifier matching CFBundleIdentifier so
# macOS seals the Info.plist into the signature — required for Spotlight indexing.
codesign --force --sign - \
    --identifier "com.pstexplorer.app" \
    --entitlements "$SCRIPT_DIR/macos-app/PSTExplorer.entitlements" \
    --options runtime \
    "$APP_BUNDLE"

echo ""

# ── Step 5: Clean up ──────────────────────────────────────────────────────────
rm -rf "$SCRIPT_DIR/publish"

# ── Step 6: Print summary ─────────────────────────────────────────────────────
APP_SIZE=$(du -sh "$APP_BUNDLE" | cut -f1)
echo "══════════════════════════════════════════════════════════════"
echo "  Build complete!"
echo ""
echo "  Output:  $APP_BUNDLE"
echo "  Size:    $APP_SIZE"
echo "  Arch:    $RID"
echo ""
echo "  To install:"
echo "    cp -R \"$APP_BUNDLE\" /Applications/"
echo ""
echo "  To run directly:"
echo "    open \"$APP_BUNDLE\""
echo ""
echo "  Data directory: ~/Documents/PST Explorer/"
echo "    ├── pst_files/    (drop PST/OST files here)"
echo "    └── exports/      (exported PDFs appear here)"
echo "══════════════════════════════════════════════════════════════"
