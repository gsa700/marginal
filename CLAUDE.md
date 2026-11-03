# Working in this repository

This repository is PUBLIC (since 2026-10-31, with its whole history).
Everything committed and pushed here is published: it can be copied, and a
force-push does not fully take it back. The rules are Deadwax's and AlbumWall's.

## Privacy: before every commit and every push

1. **Author email is the GitHub no-reply address**, never a personal one. In
   every clone, on every machine, before the first commit:

       git config user.email "298062495+gsa700@users.noreply.github.com"
       git config user.name  "David Erickson"

   Check with `git log -1 --format='%ae %ce'` after committing.
2. **No private network details in any file or commit message**: no LAN IP
   addresses (10.x, 192.168.x, 172.16-31.x), no share paths built on them.
   Use the machine's NAME instead (Techbench, Hambench, NASBOX are fine).
3. **No personal email, keys, tokens or passwords** anywhere, including
   test notes and the spec. An AcoustID or other API key never goes in the
   repository.
4. **Scan before pushing**:

       git log origin/main..HEAD --format='%ae%n%ce' | sort -u      # only the no-reply address
       git diff origin/main..HEAD | grep -nE '\b(10(\.[0-9]{1,3}){3}|192\.168(\.[0-9]{1,3}){2}|172\.(1[6-9]|2[0-9]|3[01])(\.[0-9]{1,3}){2})\b'

## What this is

The spec is `docs/spec.md`; what comes next is `docs/roadmap.md`. Read the
spec's §2 (the rules) before touching anything that writes to a music file:
nothing is written until Write, the audio is never touched, and every write
is backup → copy → read back → rename.

Code shared with Deadwax or AlbumWall is copied, with a comment in both
places saying so; a fix in one goes to the other.

## Releases (from Deadwax's)

- **Two channels.** Every build goes out on **Edge** as a GitHub pre-release:
  `tools/release.sh X.Y.Z --edge --publish`, run on Control (the Mac build is
  made, signed with the Developer ID and notarized there by
  `tools/make-macos-app.sh`; Linux and Windows come from CI's Native AOT run
  of the release commit). Copies set to Edge (Preferences) are offered it.
- **Promotion to Stable** is the same binary with its pre-release flag
  cleared: `tools/promote.sh X.Y.Z`. Dave's call, every time.
- Bump `<Version>` in `Directory.Build.props` in a commit of its own first.
  `tools/release.sh X.Y.Z` without `--publish` makes a DRAFT; `--dry-run`
  builds and packs only.
- Publishing is Dave's call, every time.
- The libraries beside the program are listed in three places that must agree:
  `InstallLayout.NativeLibraries`, `tools/release.sh` (`native_libraries`) and
  `tools/install.sh`.
- License texts: `tools/fetch-licenses.py` (AlbumWall's, less SQLite); the
  components are named in `THIRD-PARTY-NOTICES.md`.

## Spindle, the code the apps share

`spindle/` is a git submodule: https://github.com/gsa700/spindle (public,
GPLv3), compiled into the app. Marginal takes Hymie (`Spindle.Codecs`,
`Spindle.Playback`), the helpers in `Spindle.Core` (namespace `Spindle`) and
the install and update code in `Spindle.Install` and the window code in
`Spindle.Ui` from it; `Program.Identity`
(an `AppInfo`) tells Spindle which app this is, first thing in Main. Hymie which replaced the copies in `src/Marginal.Codecs` and
`src/Marginal.Playback` on 2026-11-02 (and brought the equalizer and
EndAfterCurrent AlbumWall had added since). Clone with
`--recurse-submodules` (or `git submodule update --init`); set the no-reply
author in `spindle/` too. A change to Hymie is made and tested in Spindle,
pushed there, then taken here by moving the pin (`git -C spindle pull`, build,
test, commit the `spindle` change). `tools/release.sh` refuses unless
`spindle/` is at its pinned commit and that commit is on GitHub.

## Copied code

Kept identical to its source, each file saying so; a fix in one goes to the
other: `ImageSize.cs` (AlbumWall) and `MacWindow.cs` (Deadwax). (`WebLimits`,
`AppVersion`, `VersionOrder`, `ReleaseFeed`, `LogFile` and `Diagnostics` come
from Spindle.Core since 2026-11-02, and the install and update code
(`InstallService`, `UpdateService`, `MacUpdate`, `WindowsShell`, `PowerShell`
and the rest of what was `src/Marginal.App/Install`) from Spindle.Install,
told it is Marginal by `Program.Identity`.) Made from a source and then
changed: `MusicBrainzClient.cs` (Deadwax's), the install dialogs
`InstallFlow.cs` and `MacInstaller.cs`, and `tools/` (Deadwax's); `TextSize.cs`
and the Preferences sidebar (`PrefsWindow*`) (AlbumWall's); (`WindowPlacement`,
`WaylandShell`, `WindowButtons`, `DrawnCaptions` and `ConsoleLogSink` come from
Spindle.Ui since 2026-11-02); `DrawnFrame.cs` (Deadwax's frame: corners, window buttons, resize band, title-bar drag and double click; Marginal adds the shadow and outline layers).
