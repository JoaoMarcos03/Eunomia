#!/usr/bin/env bash
# Pacotes do Eunomia para Linux e macOS (Fase 14), a partir de uma pasta de publish já feita.
#   Linux: artifacts/Eunomia-<versão>-<rid>.tar.gz com a pasta eunomia/ (executável, bibliotecas nativas, ícone,
#          eunomia.desktop, install.sh que instala no perfil do utilizador e README.md).
#   macOS: artifacts/Eunomia-<versão>-<rid>.zip com Eunomia.app (Info.plist, ícone .icns, assinatura ad-hoc).
# Sem assinatura nem notarização da Apple (os certificados não estão no repo): ver docs/user-guide.md.
#
# Uso: build/package-unix.sh <rid> <pasta do publish> <versão>
set -euo pipefail

rid="$1"
publish="$2"
version="$3"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts"
name="Eunomia-$version-$rid"
stage="$out/$name"
rm -rf "$stage"
mkdir -p "$stage"

copy_binaries() {
    # Executável e bibliotecas nativas, sem símbolos de debug.
    cp -R "$publish"/. "$1/"
    find "$1" \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) -prune -exec rm -rf {} +
}

case "$rid" in
linux-*)
    app="$stage/eunomia"
    mkdir -p "$app"
    copy_binaries "$app"
    chmod +x "$app/Eunomia"
    cp "$root/assets/brand/png/eunomia-256.png" "$app/eunomia.png"
    cp "$root/docs/user-guide.md" "$app/README.md"
    cat > "$app/eunomia.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Eunomia
GenericName=Database client
Comment=MySQL and MariaDB client
Exec=Eunomia %F
Icon=eunomia
Terminal=false
Categories=Development;Database;
StartupWMClass=Eunomia
EOF
    cat > "$app/install.sh" <<'EOF'
#!/usr/bin/env bash
# Instala o Eunomia no perfil do utilizador (sem sudo): ~/.local/opt/eunomia, atalho no menu e ícone.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
target="$HOME/.local/opt/eunomia"
mkdir -p "$target" "$HOME/.local/bin" "${XDG_DATA_HOME:-$HOME/.local/share}/applications" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
cp -R "$here"/. "$target/"
ln -sf "$target/Eunomia" "$HOME/.local/bin/Eunomia"
sed "s|^Exec=Eunomia|Exec=$target/Eunomia|" "$here/eunomia.desktop" > "${XDG_DATA_HOME:-$HOME/.local/share}/applications/eunomia.desktop"
cp "$here/eunomia.png" "${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps/eunomia.png"
echo "Eunomia installed in $target (menu entry and ~/.local/bin/Eunomia)."
EOF
    chmod +x "$app/install.sh"
    tar -C "$stage" -czf "$out/$name.tar.gz" eunomia
    echo "$out/$name.tar.gz"
    ;;
osx-*)
    app="$stage/Eunomia.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    copy_binaries "$app/Contents/MacOS"
    chmod +x "$app/Contents/MacOS/Eunomia"
    iconset="$stage/eunomia.iconset"
    mkdir -p "$iconset"
    png="$root/assets/brand/png"
    cp "$png/eunomia-16.png" "$iconset/icon_16x16.png"
    cp "$png/eunomia-32.png" "$iconset/icon_16x16@2x.png"
    cp "$png/eunomia-32.png" "$iconset/icon_32x32.png"
    cp "$png/eunomia-64.png" "$iconset/icon_32x32@2x.png"
    cp "$png/eunomia-128.png" "$iconset/icon_128x128.png"
    cp "$png/eunomia-256.png" "$iconset/icon_128x128@2x.png"
    cp "$png/eunomia-256.png" "$iconset/icon_256x256.png"
    cp "$png/eunomia-512.png" "$iconset/icon_256x256@2x.png"
    cp "$png/eunomia-512.png" "$iconset/icon_512x512.png"
    cp "$png/eunomia-1024.png" "$iconset/icon_512x512@2x.png"
    iconutil -c icns "$iconset" -o "$app/Contents/Resources/eunomia.icns"
    rm -rf "$iconset"
    short="${version%%-*}"
    cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Eunomia</string>
    <key>CFBundleDisplayName</key><string>Eunomia</string>
    <key>CFBundleIdentifier</key><string>org.eunomia.Eunomia</string>
    <key>CFBundleExecutable</key><string>Eunomia</string>
    <key>CFBundleIconFile</key><string>eunomia</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$short</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSRequiresAquaSystemAppearance</key><false/>
</dict>
</plist>
EOF
    cp "$root/docs/user-guide.md" "$stage/README.md"
    # Assinatura ad-hoc: os binários arm64 têm de estar assinados para correr.
    codesign --force --deep --sign - "$app"
    (cd "$stage" && ditto -c -k --keepParent Eunomia.app "$out/$name.zip")
    (cd "$stage" && zip -q "$out/$name.zip" README.md)
    echo "$out/$name.zip"
    ;;
*)
    echo "RID não suportado: $rid" >&2
    exit 1
    ;;
esac
