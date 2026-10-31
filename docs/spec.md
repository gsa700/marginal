# Marginal — spec

*Notes in the margins of your music library.*

Marginal is a small desktop editor for the tags, cover art and lyrics of a
music library you own. It is the third of a family — Deadwax rips CDs into a
library, AlbumWall plays it — but it is a general tool: it works on any
library of FLAC, MP3 and M4A files, whoever or whatever made them.

The name is meant two ways: what it does (the notes in the margins of each
album), and a little modesty about it.

Status: spec and mockups only (2026-10-31). Nothing is built.
Mockups: four screens, on a private design canvas (Tags & art, Lyrics,
MusicBrainz lookup, Art picker).

---

## 1. What it is, and what it is not

**It is** a clean room for an album's metadata: open an album, see exactly
what is stored in every file, fix it — by hand, from MusicBrainz, from the
Cover Art Archive, from LRCLIB — see every change before it is made, and
write it safely.

**It is not:**

- a player. It plays a track only to time lyrics.
- a library manager. It does not move, rename or delete music files, convert
  formats, or keep its own database of the library beyond what it needs to
  list albums.
- a ripper (Deadwax) or a listening app (AlbumWall). AlbumWall stays for
  listening ("if we add too much we will wreck that"); tag and art editing
  live here.

## 2. The rules (the clean room)

These hold on every screen and are the reason the app exists.

1. **Nothing is written until Write.** Every edit, lookup and fetch adds to
   one **Pending** list per album. Pending shows each change as
   `file · field · from → to`. Discard empties it. Write applies it.
2. **Every change is a kind, shown in colour and in words:**
   `+ add` (green), `~ change` (orange), `− remove` (red), and `held back`
   (red) for something found but not safe to apply without a person deciding.
   Colour never carries meaning alone: the word or sign is always there.
3. **Status the same way:** clean (green), fixable (orange), missing (red),
   neutral (grey). An album's status is the worst of its files'.
4. **The audio is never touched.** Only tag blocks and pictures change. For
   FLAC the stored audio MD5 must match before and after.
5. **Safe writes, AlbumWall's TagWriter pattern:** back up the old tags and
   art first; edit a copy beside the file; read the copy back and check it
   says what was asked, with the same audio and every other tag unchanged;
   then replace the original in one rename. A power cut leaves the old file
   or the new one, never half of each.
6. **The modified time moves, on purpose.** Backups, sync jobs, Navidrome and
   AlbumWall's index all learn of the change that way.
7. **Undo** reverses the last Write from its backup, file by file.
8. **What is really stored is always one click away.** Every field shows its
   friendly name and the actual key in that file's format (`TPE2`,
   `ALBUMARTIST`, `aART`). A Raw view lists every frame, including unknown
   ones, duplicates and junk.

## 3. Screens

Four, from the mockups. One window with two tabs (Tags & art, Lyrics) and two
sheets (MusicBrainz lookup, Art picker).

### 3.1 Tags & art (the main window)

- **Library list** (left): albums under the chosen folder, a filter box,
  format chips (FLAC / MP3 / M4A with counts), totals by status, and a short
  status per album (`clean`, `3 fixable`, `no art`, `art 300 px`).
- **Album** (centre):
  - header: title, artist, year, tracks, length; format and tag-version
    chips (`MP3 · 320 kbps`, `ID3v2.3 ×8 · v2.4 ×1`); issue counts.
  - **Album fields**: one row per field with the value across all files
    (`Jailbreak ×8, Jailbreak (Remastered) ×1` when they differ), pending
    edits as old (struck) → new, and the key it is stored as.
  - **Tracks**: number, title, length, tag versions, embedded art size,
    lyrics state (synced / plain / none), and a per-track check
    (`ID3v1 leftover`, `album differs`, `no album artist`).
  - **Pending**: the diff, with Discard and Write N files.
- **Art** (right): front `cover.jpg` and the embedded art side by side with
  size, format, bytes, square or not, and whether they differ; back.jpg
  status; an embed choice (embed cover.jpg in every file / keep cover.jpg
  only / leave as is); the selected file's raw frames, junk struck through.
- **Toolbar**: Open folder, Look up on MusicBrainz, Find art, Clean junk
  tags, Fields / Raw frames, Undo.
- **Status bar**: backup on, audio never touched, undo depth, formats.

### 3.2 Lyrics

- **Tracks** (left): each with what it has now and what a search found
  (`plain → synced found`, `none → check: +88 s`).
- **Editor** (centre): Plain / Synced / Side by side. Synced is a table of
  time and line; the playing line is highlighted. A "vs. current" column
  marks lines same / differs / new. Tools: play/pause and position, **Tap to
  time** (Space stamps the next line as it plays), ±0.1 s nudge, Shift all.
- **Found for this track** (right): the search (artist, title, album,
  duration) and results ranked by fit, each with duration difference, album,
  synced or plain, and Use this / Preview.
- **Find lyrics for the album** searches every track at once. A result is
  queued automatically only when it agrees on duration within 2 s and on
  artist and title; anything else is `held back` for a person to pick (a live
  version is the usual reason).
- **Write lyrics to**: the file's lyrics tag (default on); a synced `.lrc`
  beside the track (default on); time codes inside the tag (default off: some
  players show them as text).

### 3.3 MusicBrainz lookup (sheet)

- One screen, not a wizard. A status line in the header says where it is
  (`found 5 releases · 1996 CD selected · 9 of 9 tracks paired · 5 changes
  chosen`).
- **Find**: the query built from the tags, editable; Paste a MusicBrainz
  link; Identify by sound (AcoustID); and a **disc ID** when the folder has a
  `.toc` or `.cue` (Deadwax and EAC rips do), which matches the exact
  pressing.
- **Releases** (left), best fit first, each with a fit score and pills:
  tracks `9 of 9` / `9 of 21`, lengths `within 1 s` / `3 tracks off 2–4 s`,
  Cover Art Archive art `front + back` / `front only` / `no art`.
- **Track pairing**: your file beside the MusicBrainz track, both lengths,
  fit (`exact`, `−1 s`, `title case differs`). Paired by track number and
  length; drag to re-pair.
- **What to take**: a checklist, each row your value → theirs, tagged
  `+ add` / `~ change` / `same` / `in Pending`. Off by default: Label (a
  reissue's label is not the album's) and Genre (community votes).
  Year: the album's first release (default) or this pressing.
- Footer: **Add N changes to Pending** — nothing is written from the sheet.

### 3.4 Art picker (sheet)

- Slots: **Front**, **Back**, **Booklet**, each showing its state.
- Candidates from the Cover Art Archive for the release the files are tagged
  with first, then other editions (toggle), then larger. Each card shows its
  true shape, size, format, bytes, and pills: `this pressing` /
  `other edition`, `good size` / `small` / `too small` / `PNG, big file`.
- Also: drop an image, From a file, Paste.
- **Preview** with a square-crop outline. Shape: **Keep as printed**
  (default; a CD back inlay is about 1.27 : 1 and AlbumWall shows it whole),
  Crop to square, Pad to square.
- Output: longest side (as is / 1200 / 1000), JPEG quality (keep original /
  92 / 85), strip camera and editor metadata and convert to sRGB (default on).
- Save as: `cover.jpg` / `back.jpg` in the album folder; optionally embed
  (the cost shown: `+412 KB × 9`). Adds to Pending; the old image goes into
  the backup.

## 4. Formats and fields

FLAC (Vorbis comments + PICTURE blocks), MP3 (ID3v2.3 and v2.4; ID3v1 and
APE read and offered for removal), M4A (MP4 atoms) — all three from the
first release; most libraries have some of each.

Field names follow MusicBrainz Picard's mapping, so files Marginal writes
read the same in Picard, Navidrome, AlbumWall and most players. To be
checked against Picard's published mapping table when the field list is
built.

| Field | FLAC | MP3 (ID3v2) | M4A |
|---|---|---|---|
| Title | `TITLE` | `TIT2` | `©nam` |
| Artist | `ARTIST` | `TPE1` | `©ART` |
| Album | `ALBUM` | `TALB` | `©alb` |
| Album artist | `ALBUMARTIST` | `TPE2` | `aART` |
| Track | `TRACKNUMBER`, `TRACKTOTAL` | `TRCK` | `trkn` |
| Disc | `DISCNUMBER`, `DISCTOTAL` | `TPOS` | `disk` |
| Date | `DATE` | `TDRC` (v2.4), `TYER` (v2.3) | `©day` |
| Original date | `ORIGINALDATE` | `TDOR` (v2.4), `TORY` (v2.3) | freeform |
| Genre | `GENRE` | `TCON` | `©gen` |
| Label | `LABEL` | `TPUB` | freeform |
| Catalog no. | `CATALOGNUMBER` | `TXXX:CATALOGNUMBER` | freeform |
| Barcode | `BARCODE` | `TXXX:BARCODE` | freeform |
| MusicBrainz IDs | `MUSICBRAINZ_*` | `TXXX:MusicBrainz …`, `UFID` | freeform |
| Lyrics | `LYRICS` (read `UNSYNCEDLYRICS` too) | `USLT` | `©lyr` |
| Cover art | PICTURE block | `APIC` | `covr` |
| ReplayGain | `REPLAYGAIN_*` | `TXXX:REPLAYGAIN_*` | freeform |

"Freeform" is an iTunes `----:com.apple.iTunes:<NAME>` atom.

ID3 version: keep each file's version unless asked; v2.4 when creating a tag.
ReplayGain values are shown and kept, never recomputed (Deadwax measures them
at rip time).

## 5. Sources

All lookups are on request, never in the background, and send only what the
lookup needs (artist, title, album, duration, IDs).

- **MusicBrainz**: release search, release by ID or link, disc ID lookup.
  One request a second and a proper User-Agent, as their rules require;
  Deadwax's `MusicBrainzClient` and `WebLimits` already do this.
- **Cover Art Archive**: front, back, booklet for a release and its release
  group; Deadwax's `CoverArt` and `BackCover` already do this.
- **LRCLIB** (lrclib.net): free, community-run synced and plain lyrics by
  artist, title, album and duration. Lyrics are copyrighted; Marginal fetches
  only when asked, for files the person owns, and says where they came from.
- **AcoustID**: identify by sound. Needs Chromaprint (`fpcalc`) to
  fingerprint, and an application API key. Later step, not first release.
- **Disc ID**: from a `.toc` or `.cue` in the folder, or a Deadwax/EAC log.

## 6. With AlbumWall and Deadwax

Joined, not merged. Each works without the others.

- **AlbumWall**: an "Edit album…" entry, shown only when Marginal is
  installed, opens Marginal on that album's folder (`marginal <folder>`).
  Marginal's title bar says where it was opened from. After a Write,
  AlbumWall sees the moved modified time and re-reads the album, as it
  already does. AlbumWall keeps its own small lyrics edit for singing along.
- **Deadwax**: Marginal reads its `.toc` / log for an exact disc ID match.
  It reuses Deadwax's MusicBrainz and Cover Art Archive code.
- **Shared code**: by copy with a cross-reference comment, as AlbumWall's
  FLAC decoder is a copy of Deadwax's, until a shared library is worth it.

## 7. Platform and build

- .NET 10, Avalonia 12, Native AOT; macOS (Apple silicon), Linux x64,
  Windows x64 — the same as Deadwax and AlbumWall.
- **TagLib# 2.3.0** (LGPL 2.1) for reading and writing all three formats; it
  already runs under Native AOT in AlbumWall (kept whole for the trimmer).
- Lyric timing needs playback: reuse AlbumWall's decoders (FLAC, MP3, AAC)
  and a minimal output, or the platform's player for preview only.
- Images: decode, crop, resize, re-encode JPEG, strip metadata, sRGB.
  Library to be chosen (SkiaSharp is already in the Avalonia stack).
- Updates and channels (Edge, Stable) as in Deadwax and AlbumWall.
- License GPLv3. Third-party notices as in the siblings.
- Repository private for now; public later. The author email is the GitHub
  no-reply address from the first commit, so it can simply be made public.

## 8. Look

Light and technical, unlike the other two: a cool neutral ground, a strict
grid, Inter for labels and JetBrains Mono for values and keys, one blue
accent for the primary action and selection, and the status colours of §2.
Dark mode to follow the system, with the same meanings.

## 9. Open questions

1. **Where backups live**: a central folder in the app's data directory
   (recommended: nothing extra in the library for Navidrome or sync jobs to
   pick up), or a hidden folder beside each album.
2. **Library-wide actions**: "Clean junk tags" on one album only, or across
   the library with a preview of every album it would touch?
3. **Renaming files to match tags**: out of scope, or a later, separate,
   opt-in step?
4. **Booklet** images: save where, and under what names?
5. **Playback for lyric timing**: share AlbumWall's codecs, or preview through
   the platform?
6. **Network shares**: test against SMB on all three platforms early; a
   rename on a share is not always atomic.
