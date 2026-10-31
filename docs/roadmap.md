# Roadmap

What comes next, in order. The spec is `docs/spec.md`.

## Steps

0. **Skeleton.** *Done 2026-10-31 (3f81aa8), CI green on all three.* Solution and projects (`Marginal.App`, `Marginal.Core`,
   `Marginal.Tests`), Directory.Build.props, CI on all three platforms with
   Native AOT, the light theme, an empty main window. Privacy rules copied
   from Deadwax; its release scripts wait for step 7.
1. **Read and show.** *Done 2026-10-31: tried on Control's ~/Music (386
   albums). Also: the cover from the files when there is no cover.jpg,
   resizable panes, square within 1%, track-level fields out of the album
   table.* Open a folder, list albums, show an album's fields,
   tracks and raw frames for FLAC, MP3 and M4A through TagLib#. The status
   checks (ID3v1 leftover, values that differ across tracks, missing album
   artist, embedded art vs cover.jpg). Read only: nothing can be written yet.
2. **Pending and Write.** *Done 2026-10-31, but for library-wide actions
   (moved to step 2b): tested by Dave on copies (scratch folder), and by
   round trips on copies of three real albums, byte-identical after Undo.
   The backup is the whole original file, moved aside.* Edit fields by hand; the Pending list with
   `+ ~ −`; the safe write (backup, copy, read back, rename) with backups in
   the central app folder; Undo. Tested on copies of real albums of each
   format. A library on a network share opens read-only, Write off, with a
   note saying why. Library-wide actions with their full preview.
2b. **Library-wide actions and backup upkeep.** *Done 2026-10-31: tried by
   Dave on copies (clean, Write all, Undo, re-clean).* "Clean junk tags" (and
   the like) across the library, every album and change shown first. Backups
   were whole files and added up (two test writes: 364 MB); since 2026-10-31
   they keep only what is not audio (~100 KB an album), Undo rebuilds the
   original byte for byte, and they are kept a year. Size on screen, an
   "Empty backups" button. MP4 junk atoms too.
3. **MusicBrainz lookup.** *Done 2026-10-31 but for disc IDs and
   per-recording IDs (later): search, pasted links, the files' own release
   first, ranking (every release scores 100, so: tagged, track count, CD
   and digital, score), pairing with lengths, the checklist (Label unticked;
   nothing ticked when the fit is under 90%), first-release or pressing
   year. Tried by Dave on copies.* Search from tags, link, disc ID from `.toc`/`.cue`;
   release ranking; track pairing; what-to-take checklist into Pending.
4. **Art picker.** *Done 2026-10-31 but for booklet pages and paste (later):
   Cover Art Archive candidates for the files' release and up to 8 other
   editions, shown as they arrive (an index takes 1.5-5 s), file or drop,
   keep / crop / pad, size, quality, metadata strip without re-encoding,
   cover.jpg / back.jpg and embedding the front, all through Pending with
   exact Undo.* Cover Art Archive candidates, file/drop/paste, crop and
   resize, save and embed, into Pending.
5. **Lyrics.** Show and edit plain and synced lyrics; LRCLIB search per track
   and for the album with the 2 s rule; `.lrc` sidecar. Then playback and Tap
   to time.
6. **AlbumWall hook.** `marginal <folder>` on the command line; AlbumWall's
   "Edit album…" when Marginal is installed.
7. **First release** on Edge: packaging, signing, notarizing, updater, as in
   the siblings.

## Later

- Identify by sound (AcoustID + Chromaprint).
- Rename files and folders to match tags (opt-in, previewed in Pending).
- Dark mode.

## Decided

- 2026-10-21: tag and art editing is a separate small app, not part of
  AlbumWall or Deadwax.
- 2026-10-31: MP3 and M4A from the start, not only FLAC. A lighter, technical
  look ("a media clean room"). Joined to AlbumWall but not built into it.
  A general tool, not only for Deadwax or AlbumWall users.
- 2026-10-31: named **Marginal** ("notes in the margins of your music
  library"). Runner-up: Tagbench. Not Tagroom, TagLab or Obi (names in use).
- 2026-10-31: the MusicBrainz lookup is one screen with a status line, not a
  wizard.
- 2026-10-31: a back cover is kept as printed by default, not cropped square.
- 2026-10-31: lyrics found automatically are applied only when the duration
  agrees within 2 s; the rest are held back for a person.
- 2026-10-31, the spec's open questions (spec §9): backups in a central app
  folder; library-wide actions with a full preview; renaming files later and
  opt-in; booklet images in `booklet/NN.jpg`; lyric playback by copying
  AlbumWall's decoders; network shares read-only (write to local libraries
  only).
