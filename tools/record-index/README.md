# record-index

An encrypted, searchable index of the JJ Flexible historical record on the NAS (task 854). A search returns the archive, the member inside it, the line, and a short snippet, so only the few archives that matter get opened, instead of unpacking the whole record to grep it.

The index is private data: it holds the words of session transcripts, NVDA logs and private documents. It never lives in this repository (the tool refuses a home inside any git working tree), and the whole database file is encrypted.

## What it covers

Every zip in `\\nas.macaw-jazz.ts.net\jjflex\historical`, plus loose text files there (build NOTES, NVDA logs, stats). Not `dev-mirror` (a rolling copy of C:\dev, not history), `dev-orphans`, `old` (installers), or the firmware extraction under `research`. The policy lives in `Indexing/RecordScanner.cs`.

The record is only ever read. Archives are opened read-only with read sharing; nothing in the tool can modify, move, re-zip or delete one.

## Commands

Searching:

- `record-index search WORDS` lists the best matches. Each result names the archive and its date, the member and its kind, the line number, when it was written (for transcripts), how many other archives hold the same text and where it was first seen, a snippet, and the exact extract command to read it.
- Words are ANDed. `"a phrase"` stays a phrase. `word*` is a prefix. `--any` ORs the words. `--fts` passes SQLite FTS5 syntax through untouched.
- Filters: `--kind` (an archive kind such as `claude-state`, `memory`, `private`, `appdata`, `build`, `nvda-log`, or a member kind such as `transcript`, `memory-entry`, `trace`), `--since DATE`, `--until DATE` (inclusive), `--path TEXT`. `--json` for machine output.
- `record-index search` with no words keeps the index open and asks for one search after another, so one unlock serves a whole session. Enter on an empty line locks it again.
- `record-index extract ARCHIVE MEMBER --line N` prints the lines around a hit; `--out FILE` writes the whole member. Extract reads the record directly and needs no key.

Keeping it current:

- `record-index catch-up` merges anything staged, indexes every archive that is new or changed, and drops archives that were pruned from the record.
- `record-index add ARCHIVE` is the seal's hook. It indexes one archive into an encrypted stage and needs no key at all; the next unlocked catch-up folds it in.

Keys: `init`, `keys list`, `keys add-recovery`, `keys add-yubikey`, `keys remove ID`, `keys rotate`. See "How the encryption works" below.

Everywhere: `--home` (default `\\nas.macaw-jazz.ts.net\jjflex\record-index`, or `JJFLEX_RECORD_INDEX_HOME`), `--record`, `--unlock auto|yubikey|recovery`, `--recovery-key-from prompt|clipboard|file:PATH`.

## How the index is shaped

The record is mostly repeats: every memory, private and Claude-state snapshot is the previous one plus a little. So storage is content-addressed in levels: an archive has members; a member points at a body (one distinct content, by SHA-256); a body is an ordered list of chunks (about 8 KB of text each, cut at line boundaries); each distinct chunk is stored once, Brotli-compressed, and is one row of a contentless FTS5 table. An unchanged memory entry across 120 snapshots is one chunk. A session transcript that grew overnight reuses every chunk but its last, because chunking is deterministic from the start of the file and transcripts only grow at the end. A member whose path, CRC-32 and size match one already indexed is not even decompressed.

Session transcripts are indexed by their decoded string values, not their raw JSON, so line breaks inside messages do not glue words together; bookkeeping keys (ids, signatures) and long encoded blobs are left out. Each chunk keeps a map back to source lines, so a hit resolves to an exact line and byte offset.

## How the encryption works

- The database file is encrypted page by page with SQLite3 Multiple Ciphers in its SQLCipher 4 compatible scheme, under a random 256-bit key. Full-text search needs plain words inside the database, so the whole file is encrypted rather than columns.
- That key is stored only wrapped, in `record-index.keyring.json` beside the database: once per unlocking key pair, with ECDH P-256, HKDF-SHA256 and AES-256-GCM. The keyring holds public keys and wraps, nothing secret in the clear.
- The unlocking key pairs are a YubiKey (a P-256 key generated inside its PIV applet, slot 9D by default, which cannot be copied out) and a recovery key whose private half lives in 1Password. No key is held by the TPM or by Windows.
- Adding or removing an unlocking key re-wraps the 32-byte database key; the database is not touched. `keys rotate` replaces the database key itself, for when a key may have been exposed.
- The seal's hook stages an archive to a staging public key. Its private half is stored inside the encrypted database, so a stage is readable only by an unlocked index, and staging never needs the YubiKey or the recovery key.
- A stage cannot see the index, so it compares against the previous archive in the same series, read straight from the record, and records unchanged members by reference instead of reading them. The merge resolves those references; if the previous archive is not in the index, the stage is set aside and the archive is indexed from the record instead.

## Tests

`dotnet test tools/record-index/RecordIndex.Tests/RecordIndex.Tests.csproj -c Debug`

Synthetic archives only, built in a temporary folder. No test reads the real record, touches the clipboard, or touches a YubiKey: the YubiKey path runs against `SoftwarePivDevice`, a stand-in implementing the same `IPivDevice` interface. `YubiKeyPivDevice` is therefore never exercised automatically; its first real run is the operator's enrollment.

This project is deliberately not in `JJFlexRadio.sln`: it brings a native SQLite build and the Yubico SDK that the application has no use for.
