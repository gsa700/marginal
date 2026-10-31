#!/usr/bin/env bash
# Marginal - build Marginal.app, the Mac's form of the program. On a Mac only.
#
# FROM Deadwax's tools/make-macos-app.sh (2026-10-31), renamed; dates and
# findings in these notes are Deadwax's history. A fix here goes to both.
#
#   tools/make-macos-app.sh            dist/Marginal.app, for the machine's own arch
#   tools/make-macos-app.sh --install  and copy it to /Applications (replacing one there)
#   tools/make-macos-app.sh --notarize also have Apple notarize it and staple the ticket
#                                      (needs the Developer ID; options combine)
#   tools/make-macos-app.sh --dmg      also dist/Marginal.dmg, the download for people: a window
#                                      with the app and an Applications folder to drag it to
#                                      (signed; notarized and stapled too with --notarize)
#
# WHAT IS IN IT: the program, compiled ahead of time (Native AOT, since 0.8;
# the single-file build before), in the folder layout macOS expects of an
# application, with its native libraries (Avalonia's and Skia's; no
# libcdio or libcdio-paranoia since 0.7.2, when Marginal began reading the
# drive with its own code on the Mac) beside it in the bundle, each signed
# with the app's own certificate:
#
#   Marginal.app/Contents/Info.plist        name, version, identity, icon
#   Marginal.app/Contents/MacOS/Marginal     the program
#   Marginal.app/Contents/MacOS/*.dylib     its native libraries
#   Marginal.app/Contents/Resources/Marginal.icns
#
# NOT UNPACKED INTO ~/.net, as release.sh's builds for the other platforms
# are (IncludeNativeLibrariesForSelfExtract): that folder is the user's to
# write, the runtime reuses what it finds there, and the unpacked libraries
# were not signed by our team, so the app needed library validation off,
# and then any library put there ran inside the notarized app with its
# permissions (security review 2026-10-24, M1). Inside the sealed bundle,
# signed by our team, library validation stays on.
#
# Installing on a Mac IS putting that folder in /Applications: Finder,
# Launchpad, Spotlight and the Dock find it there. AlbumWall's
# scripts/make-macos-app.sh is the model; the two stay alike.
#
# SIGNED WITH APPLE'S DEVELOPER ID when this Mac has that certificate (since
# 2026-10-23, team 4QX5GG2276), else with a local certificate, else AD HOC.
# With the Developer ID it gets the hardened runtime, and --notarize has Apple
# notarize it, which a DOWNLOADED copy needs to get past Gatekeeper. macOS remembers a permission (the Music folder the rips go into)
# against the app's signature; ad hoc, every rebuild would ask again. Without a
# Developer ID, make a code-signing certificate once in Keychain Access
# (Certificate Assistant, Create a Certificate: Self-Signed Root, Certificate
# Type Code Signing), named "Marginal Local", or name any certificate in
# MARGINAL_SIGN_ID.
#
# The identity, io.github.gsa700.Marginal, is where macOS keeps the app's
# permissions and preferences, so it must not change.
#
# Proven 2026-10-15 on the first Mac: subchannel, read and rip of the
# Honeydrippers reference disc matched whipper's (G1-G5), eject works.

set -euo pipefail

[ "$(uname -s)" = Darwin ] || { echo "make-macos-app.sh: on a Mac only" >&2; exit 1; }

INSTALL=no; NOTARIZE=no; DMG=no
for arg in "$@"; do
    case "$arg" in
        --install) INSTALL=yes ;;
        --notarize) NOTARIZE=yes ;;
        --dmg) DMG=yes ;;
        *) echo "usage: $0 [--install] [--notarize] [--dmg]" >&2; exit 2 ;;
    esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/Marginal.App"
case "$(uname -m)" in arm64) RID=osx-arm64 ;; x86_64) RID=osx-x64 ;; *) echo "unknown arch" >&2; exit 1 ;; esac
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
BUNDLE_ID=io.github.gsa700.Marginal
MIN_MACOS=13.0      # .NET 10's floor (the natives themselves go back to 12.0)

OUT="$ROOT/dist"
APP="$OUT/Marginal.app"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT


echo "== publish $RID $VERSION (Native AOT)"
# Compiled ahead of time: one native program, no runtime inside it and no JIT
# (the road to 0.8). The native libraries are files of their own, for the
# bundle (above), exactly as the single file left them.
dotnet publish "$PROJECT" -c Release -r "$RID" -p:PublishAot=true -o "$WORK/pub" -nologo -v quiet
ls "$WORK/pub"/*.dylib >/dev/null 2>&1 || { echo "the publish left no .dylib beside the program" >&2; exit 1; }

echo "== icon"
# Rendered from icon.svg by macOS itself (sips reads SVG), every size drawn at
# that size. Apple's grid puts the artwork at 824 of 1024 with clear margin all
# round; a full-bleed square looks oversized beside every other Dock icon, so
# each size is drawn at 80.5% and centred on a transparent canvas.
ICONSET="$WORK/Marginal.iconset"
mkdir -p "$ICONSET"
for spec in 16:icon_16x16 32:icon_16x16@2x 32:icon_32x32 64:icon_32x32@2x \
            128:icon_128x128 256:icon_128x128@2x 256:icon_256x256 512:icon_256x256@2x \
            512:icon_512x512 1024:icon_512x512@2x; do
    size=${spec%%:*} name=${spec#*:}
    art=$(( size * 824 / 1024 ))
    sips -s format png -z "$art" "$art" "$PROJECT/Assets/icon.svg" --out "$WORK/art.png" >/dev/null
    sips -p "$size" "$size" "$WORK/art.png" --out "$ICONSET/$name.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$WORK/Marginal.icns"

echo "== bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$WORK/pub/Marginal" "$APP/Contents/MacOS/Marginal"
cp "$WORK/pub/"*.dylib "$APP/Contents/MacOS/"
cp "$WORK/Marginal.icns" "$APP/Contents/Resources/Marginal.icns"
# What the licenses ask to travel with the program, as in a release zip.
cp "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/"
cp -R "$ROOT/licenses" "$APP/Contents/Resources/licenses"
printf 'APPL????' > "$APP/Contents/PkgInfo"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key>
	<string>$BUNDLE_ID</string>
	<key>CFBundleName</key>
	<string>Marginal</string>
	<key>CFBundleDisplayName</key>
	<string>Marginal</string>
	<key>CFBundleExecutable</key>
	<string>Marginal</string>
	<key>CFBundleIconFile</key>
	<string>Marginal</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>$VERSION</string>
	<key>CFBundleVersion</key>
	<string>$VERSION</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>LSMinimumSystemVersion</key>
	<string>$MIN_MACOS</string>
	<key>LSApplicationCategoryType</key>
	<string>public.app-category.music</string>
	<key>NSHighResolutionCapable</key>
	<true/>
	<key>NSHumanReadableCopyright</key>
	<string>GPLv3.</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist" >/dev/null

# Which certificate: MARGINAL_SIGN_ID if set (a name or a SHA-1); else this Mac's
# Apple Developer ID Application certificate when there is exactly one (two
# while one is being renewed: then name one); else the local "Marginal Local".
if [ -n "${MARGINAL_SIGN_ID:-}" ]; then
    SIGN_ID=$MARGINAL_SIGN_ID
else
    DEVID=$(security find-identity -v -p codesigning | sed -n 's/^ *[0-9]*) \([0-9A-F]\{40\}\) "Developer ID Application: .*"$/\1/p')
    case $(printf '%s' "$DEVID" | grep -c . || true) in
        0) SIGN_ID="Marginal Local" ;;
        1) SIGN_ID=$DEVID ;;
        *) echo "several Developer ID Application certificates on this Mac: name one in MARGINAL_SIGN_ID" >&2
           security find-identity -v -p codesigning | grep "Developer ID Application" >&2; exit 1 ;;
    esac
fi
# A Developer ID signature gets the hardened runtime, which notarization
# requires, and Apple's timestamp. No exception to it since 0.8: the single
# file needed JIT (an allow-jit entitlement; without it CoreCLR did not start),
# and a Native AOT program generates no code at run time. Each library is
# signed first, the same way, and the bundle last, which seals them in.
if security find-identity -p codesigning | grep -qF -e "\"$SIGN_ID\"" -e ") $SIGN_ID "; then
    LABEL=$(security find-identity -p codesigning | grep -F -e "\"$SIGN_ID\"" -e ") $SIGN_ID " | head -1 | sed 's/^[^"]*"\([^"]*\)".*/\1/')
    echo "== sign ($LABEL)"
    case $LABEL in
        "Developer ID Application:"*)
            for lib in "$APP/Contents/MacOS/"*.dylib; do
                codesign --force --options runtime --timestamp --sign "$SIGN_ID" "$lib"
            done
            codesign --force --options runtime --timestamp --sign "$SIGN_ID" "$APP" ;;
        *)
            [ "$NOTARIZE" = yes ] && { echo "--notarize needs a Developer ID certificate, not \"$LABEL\"" >&2; exit 1; }
            for lib in "$APP/Contents/MacOS/"*.dylib; do codesign --force --sign "$SIGN_ID" "$lib"; done
            codesign --force --sign "$SIGN_ID" "$APP" ;;
    esac
else
    [ "$NOTARIZE" = yes ] && { echo "--notarize needs a Developer ID certificate" >&2; exit 1; }
    echo "== sign (ad hoc: no \"$SIGN_ID\" certificate on this Mac, so macOS will ask for its permissions again)"
    for lib in "$APP/Contents/MacOS/"*.dylib; do codesign --force --sign - "$lib"; done
    codesign --force --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

# NOTARIZE: Apple checks the signed app and issues a ticket; stapling puts the
# ticket inside the app, so a Mac that downloads it can check it offline.
# Credentials are a notarytool keychain profile (made once with `xcrun notarytool
# store-credentials`), named in MARGINAL_NOTARY_PROFILE, "notary" by default.
if [ "$NOTARIZE" = yes ]; then
    echo "== notarize (Apple; usually a few minutes)"
    ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
    xcrun notarytool submit "$WORK/notarize.zip" --keychain-profile "${MARGINAL_NOTARY_PROFILE:-notary}" --wait \
        | tee "$WORK/notary.txt"
    if ! grep -q "status: Accepted" "$WORK/notary.txt"; then
        id=$(sed -n 's/^ *id: //p' "$WORK/notary.txt" | head -1)
        [ -n "$id" ] && xcrun notarytool log "$id" --keychain-profile "${MARGINAL_NOTARY_PROFILE:-notary}" >&2
        echo "notarization was not accepted" >&2; exit 1
    fi
    xcrun stapler staple "$APP"
    xcrun stapler validate "$APP"
    spctl --assess --type execute -vv "$APP"
fi

# THE DMG, for people: opening it shows Marginal.app beside a link to
# /Applications, to drag it across. (Run from the disk image or from
# Downloads, macOS starts it from a randomised read-only copy, App
# Translocation, which also cannot update itself.)
# The image is signed like the app, and notarized and stapled with --notarize,
# so it opens with no Gatekeeper warning even offline.
#
# THE WINDOW: a background with an arrow from the app to Applications
# (tools/macos/dmg-background.svg, drawn at 1x and 2x into one TIFF), the
# icons placed on it, no toolbar, the disk with Marginal's icon. Finder lays
# it out, told through AppleScript, on the image mounted read-write; it keeps
# that in the image's .DS_Store, and the image is then compressed. The first
# time, macOS asks whether this terminal may control Finder. Without Finder
# (refused, or no one logged in) the dmg is made as before, a plain window,
# and the script says so: a release never fails on the decoration.
if [ "$DMG" = yes ]; then
    echo "== dmg"
    DMGSRC="$WORK/dmg"
    VOLNAME="Marginal $VERSION"
    rm -rf "$DMGSRC"; mkdir -p "$DMGSRC/.background"
    ditto "$APP" "$DMGSRC/Marginal.app"
    ln -s /Applications "$DMGSRC/Applications"
    sips -s format png -z 400 660 "$ROOT/tools/macos/dmg-background.svg" --out "$WORK/bg.png" >/dev/null
    sips -s format png -z 800 1320 "$ROOT/tools/macos/dmg-background.svg" --out "$WORK/bg@2x.png" >/dev/null
    tiffutil -cathidpicheck "$WORK/bg.png" "$WORK/bg@2x.png" -out "$DMGSRC/.background/background.tiff" 2>/dev/null
    rm -f "$OUT/Marginal.dmg"
    hdiutil create -quiet -volname "$VOLNAME" -srcfolder "$DMGSRC" -fs HFS+ -format UDRW -ov "$WORK/rw.dmg"
    MNT=$(hdiutil attach -readwrite -noverify -noautoopen "$WORK/rw.dmg" | awk -F'\t' '/\/Volumes\//{print $NF}' | tail -1)
    [ -d "$MNT" ] || { echo "the read-write image did not mount" >&2; exit 1; }
    trap 'hdiutil detach -force -quiet "$MNT" 2>/dev/null; rm -rf "$WORK"' EXIT   # never left mounted
    # By its mount point, not its name: another disk of the same name (the
    # dmg of the release before, still mounted) would be laid out instead.
    if osascript - "$MNT" <<'APPLESCRIPT'
on run argv
    set mountPoint to (POSIX file (item 1 of argv)) as alias
    tell application "Finder"
        tell folder mountPoint
            open
            set current view of container window to icon view
            set toolbar visible of container window to false
            set statusbar visible of container window to false
            set the bounds of container window to {200, 120, 860, 548}
            set opts to the icon view options of container window
            set arrangement of opts to not arranged
            set icon size of opts to 128
            set text size of opts to 13
            set background picture of opts to file ".background:background.tiff"
            set extension hidden of item "Marginal.app" to true
            set position of item "Marginal.app" of container window to {170, 190}
            set position of item "Applications" of container window to {490, 190}
            update without registering applications
            delay 1
            close
        end tell
    end tell
end run
APPLESCRIPT
    then
        # Finder writes .DS_Store when it gets round to it.
        for _ in 1 2 3 4 5 6 7 8 9 10; do [ -f "$MNT/.DS_Store" ] && break; sleep 0.5; done
        [ -f "$MNT/.DS_Store" ] || echo "warning: Finder wrote no layout; the dmg opens as a plain window" >&2
    else
        echo "warning: Finder could not be asked to lay out the window (allow this terminal to control Finder in System Settings > Privacy & Security > Automation); the dmg opens as a plain window" >&2
    fi
    rm -rf "$MNT/.fseventsd" "$MNT/.Trashes"
    # The disk's own icon, .VolumeIcon.icns: copied in here, since hdiutil's
    # -srcfolder leaves it out, and flagged after Finder, which clears the flag.
    if command -v SetFile >/dev/null; then
        cp "$WORK/Marginal.icns" "$MNT/.VolumeIcon.icns"
        SetFile -a C "$MNT"
        SetFile -a V "$MNT/.background" "$MNT/.VolumeIcon.icns"
        # "Marginal", not "Marginal.app": Finder's "extension hidden" above does
        # not stay (seen on AlbumWall's, 2026-10-27); the file's own flag does.
        SetFile -a E "$MNT/Marginal.app"
    fi
    sync
    hdiutil detach -quiet "$MNT" || { sleep 2; hdiutil detach -force -quiet "$MNT"; }
    trap 'rm -rf "$WORK"' EXIT
    hdiutil convert -quiet "$WORK/rw.dmg" -format UDZO -imagekey zlib-level=9 -o "$OUT/Marginal.dmg"
    if [ -n "${LABEL:-}" ]; then
        codesign --force --timestamp --sign "$SIGN_ID" "$OUT/Marginal.dmg"
        codesign --verify --strict "$OUT/Marginal.dmg"
    fi
    if [ "$NOTARIZE" = yes ]; then
        echo "== notarize the dmg"
        xcrun notarytool submit "$OUT/Marginal.dmg" --keychain-profile "${MARGINAL_NOTARY_PROFILE:-notary}" --wait \
            | tee "$WORK/notary-dmg.txt"
        if ! grep -q "status: Accepted" "$WORK/notary-dmg.txt"; then
            id=$(sed -n 's/^ *id: //p' "$WORK/notary-dmg.txt" | head -1)
            [ -n "$id" ] && xcrun notarytool log "$id" --keychain-profile "${MARGINAL_NOTARY_PROFILE:-notary}" >&2
            echo "notarization of the dmg was not accepted" >&2; exit 1
        fi
        xcrun stapler staple "$OUT/Marginal.dmg"
        xcrun stapler validate "$OUT/Marginal.dmg"
        spctl --assess --type open --context context:primary-signature -vv "$OUT/Marginal.dmg"
    fi
    du -h "$OUT/Marginal.dmg" | cut -f1
fi

echo "== $APP"
du -sh "$APP" | cut -f1
file "$APP/Contents/MacOS/Marginal"

if [ "$INSTALL" = yes ]; then
    echo "== install to /Applications"
    if pgrep -xq Marginal; then
        echo "Marginal is running: quit it first" >&2; exit 1
    fi
    rm -rf /Applications/Marginal.app
    cp -R "$APP" /Applications/Marginal.app
    # Tell Launch Services about it now rather than whenever it next looks.
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /Applications/Marginal.app
    echo "installed: /Applications/Marginal.app"
fi
