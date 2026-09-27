# Shared accounts fixture

`accounts.json` here is the pair of free-to-air accounts (`free1`, `free2`) that
ships with every build, so a fresh install has working channels on first launch
instead of an empty account list.

## What is actually in the file

The payload is encrypted. The format is a `CIPTV2` magic line followed by one
base64 blob, produced by the app's own `ConfigStore` protection. There is no
plaintext hostname, username, or password in the file, and the ciphertext is
bound to the Windows user profile that created it via DPAPI. Copying this file
to another machine does not yield usable credentials.

## Why it is committed

It is deliberately tracked, and `scripts\BUILD_WINDOWS_RELEASE.bat` copies it
into the release output. The tradeoff was accepted knowingly: the accounts are
free-to-air, the file is opaque, and the convenience of a working first launch
was judged worth it.

The cost is that every installation starts on the same two accounts. If the
provider rate-limits or bans them, that affects all users at once. Reissue them
by replacing this file and rebuilding.

## Relationship to the local test copies

The same file is seeded into `release\classic-windows-iptv-player\` and into the
private test build's output folder. `release\` is gitignored, so only the copy
in this folder is tracked; the others are local artifacts and are recreated on
each build.

If you would rather ship an empty account list, delete this file and remove the
`copy /y fixtures\accounts.json ...` line from the release script. The app
handles a missing `accounts.json` by creating a fresh one.
