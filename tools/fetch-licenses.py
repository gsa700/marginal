#!/usr/bin/env python3
# Marginal - fetch the license TEXTS of everything a release carries, verbatim.
#
# FROM AlbumWall's tools/fetch-licenses.py (2026-10-31), less SQLite (Marginal
# keeps no database). Marginal carries AlbumWall's decoders, so the same texts.
#
#   python tools/fetch-licenses.py
#
# THIRD-PARTY-NOTICES.md names every bundled component and its license. A release
# also has to carry the license texts themselves, and a license text is not
# something to type from memory or paste from wherever: it has to be the file
# the project itself ships, byte for byte, at the version that was built. This
# fetches each one from the project's own repository into licenses/, and writes
# licenses/README.md saying where every file came from, at which ref, and its
# SHA-256, so that a reader can check any of them against upstream.
#
# Until 0.7.0 the audio engine was libmpv, built from seven projects whose
# versions were read out of its build recipe here. Hymie, the engine since, is
# C#; what it ports from is pinned by commit below.
#
# It refuses to write a file that does not look like a license - an HTML error
# page saved as COPYING is exactly the failure this kind of script has.

import hashlib
import io
import os
import re
import sys
import urllib.error
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "licenses")

# (file to write, what it covers, repository, refs to try in order, paths to try in order)
# A ref of None means the repository's default branch, used only for components
# whose license does not change with the version and whose tags are not
# predictable from a package version. Which ref was used is recorded either way.
WANTED = [
    # ---- the application
    ("dotnet-runtime-LICENSE.TXT", ".NET runtime (MIT)", "dotnet/runtime", ["v10.0.0", None], ["LICENSE.TXT"]),
    ("avalonia-licence.md", "Avalonia (MIT)", "AvaloniaUI/Avalonia", ["12.1.4", None], ["licence.md", "LICENSE.md", "LICENSE"]),
    ("skiasharp-LICENSE.md", "SkiaSharp and HarfBuzzSharp (MIT)", "mono/SkiaSharp", [None], ["LICENSE.md", "LICENSE.txt", "LICENSE"]),
    ("skia-LICENSE.txt", "Skia (BSD 3-Clause)", "google/skia", [None], ["LICENSE"]),
    ("freetype-FTL.TXT", "FreeType (the FreeType License), inside Skia's Linux build", "freetype/freetype", [None], ["docs/FTL.TXT"]),
    ("freetype-LICENSE.TXT", "FreeType: its choice of licenses", "freetype/freetype", [None], ["LICENSE.TXT"]),
    ("harfbuzz-COPYING.txt", "HarfBuzz (Old MIT), inside HarfBuzzSharp", "harfbuzz/harfbuzz", [None], ["COPYING"]),
    ("microcom-LICENSE.txt", "MicroCom (MIT)", "kekekeks/MicroCom", [None], ["LICENSE", "LICENSE.md", "LICENSE.txt", "licence.md"]),
    ("taglib-sharp-COPYING.txt", "TagLib# (LGPL v2.1)", "mono/taglib-sharp", [None], ["COPYING"]),
    ("tmds-dbus-COPYING.txt", "Tmds.DBus.Protocol (MIT)", "tmds/Tmds.DBus", [None], ["COPYING", "LICENSE"]),
    ("inter-LICENSE.txt", "Inter, the typeface (SIL OFL 1.1)", "rsms/inter", [None], ["LICENSE.txt", "LICENSE"]),

    # ---- the own engine's decoders: ported, at the commit the port was made from
    ("minimp3-LICENSE.txt", "minimp3 (CC0 1.0), ported into the MP3 decoder", "lieff/minimp3",
     ["ea99364f61c14656440e8d77e9c233ccf3124633"], ["LICENSE"]),
    ("faad2-COPYING.txt", "FAAD2 (GPL v2 or later), ported into the AAC decoder", "knik0/faad2",
     ["521d105979795b887ea11e1f36565e97a8fbfc53"], ["COPYING"]),
    # The repository's own LICENSE, which its ported files' headers name; not
    # codec/APPLE_LICENSE.txt, an APSL text none of those files is under.
    ("alac-LICENSE.txt", "Apple Lossless (Apache 2.0), ported into the ALAC decoder", "macosforge/alac",
     ["c38887c5c5e64a4b31108733bd79ca9b2496d987"], ["LICENSE"]),
]

# Something every real license in the list above contains at least one of.
LOOKS_LIKE = re.compile(
    r"licen[sc]e|permission is hereby granted|redistribution and use|copyright|warranty", re.I)


def fetch(repo, ref, path):
    url = "https://raw.githubusercontent.com/%s/%s/%s" % (repo, ref or "HEAD", path)
    request = urllib.request.Request(url, headers={"User-Agent": "albumwall-fetch-licenses"})
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return url, response.read()
    except urllib.error.HTTPError:
        return url, None


def acceptable(data):
    if data is None or len(data) < 200:
        return False
    head = data[:300].lstrip().lower()
    if head.startswith(b"<!doctype") or head.startswith(b"<html"):
        return False
    return bool(LOOKS_LIKE.search(data.decode("utf-8", errors="replace")))


def main():
    os.makedirs(OUT, exist_ok=True)
    rows, failed = [], []

    for name, covers, repo, refs, paths in WANTED:
        got = None
        for ref in refs:
            for path in paths:
                url, data = fetch(repo, ref, path)
                if acceptable(data):
                    got = (url, ref, path, data)
                    break
            if got:
                break

        if not got:
            failed.append("%s (%s)" % (name, repo))
            print("FAILED  %-32s %s" % (name, repo))
            continue

        url, ref, path, data = got
        with open(os.path.join(OUT, name), "wb") as f:      # bytes in, bytes out: verbatim
            f.write(data)
        digest = hashlib.sha256(data).hexdigest()
        rows.append((name, covers, repo, ref or "default branch", path, digest, len(data)))
        print("ok      %-32s %s @ %s" % (name, repo, ref or "default branch"))

    with io.open(os.path.join(OUT, "README.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# The license texts\n\n")
        f.write("Every file here is the license file its project ships, byte for byte, fetched from\n")
        f.write("the project's own repository by `tools/fetch-licenses.py`. Nothing in this folder\n")
        f.write("was typed or edited. What each component is and what it does in Marginal is in\n")
        f.write("[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md); Marginal's own license is\n")
        f.write("[LICENSE](../LICENSE), the GNU GPL version 3.\n\n")
        f.write("The decoders' sources are pinned by the commit each was ported from. Where a\n")
        f.write("ref says *default branch*, the project's\n")
        f.write("tags could not be predicted from a package version and its license does not vary\n")
        f.write("with it; the SHA-256 says exactly which text this is.\n\n")
        f.write("| file | covers | from | ref | path | SHA-256 |\n|---|---|---|---|---|---|\n")
        for name, covers, repo, ref, path, digest, size in rows:
            f.write("| [%s](%s) | %s | https://github.com/%s | `%s` | `%s` | `%s` |\n"
                    % (name, name, covers, repo, ref, path, digest))

    print("\n%d written to licenses/, %d failed" % (len(rows), len(failed)))
    if failed:
        print("not fetched: " + "; ".join(failed))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
