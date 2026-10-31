# Working in this repository

This repository is PRIVATE for now and will be made PUBLIC later, as it is,
with its history. So everything committed here should be fit to publish from
the first commit. The rules are Deadwax's and AlbumWall's.

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
