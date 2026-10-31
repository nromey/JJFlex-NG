namespace JJFlex.RecordIndex.Store;

/// <summary>
/// The index's shape. The record is mostly REPEATS: every memory, private and
/// Claude-state snapshot is a full copy of the one before it plus a little. So
/// the schema is content-addressed in three levels, and each level is stored once:
///
///   archive  one file in the record (a zip, or a loose text file)
///   member   one entry inside it: path, size, CRC, date, kind; points at a body
///   body     one distinct member content (SHA-256 of its bytes); an ordered list
///            of chunks, each with the line number and byte offset where it starts
///   chunk    one distinct piece of text (about 8 KB), compressed, plus the map
///            from its text back to source lines; the FTS5 row is the chunk's id
///
/// A memory entry unchanged across 120 snapshots is one body and one chunk with
/// 120 member rows pointing at it. A session transcript that grew between
/// snapshots is a new body whose chunks are almost all old chunks, because
/// chunking is deterministic from the start of the file and transcripts only
/// ever grow at the end.
///
/// The FTS5 table is contentless (content='') with contentless_delete=1, so the
/// text is not stored twice, and chunks can still be deleted when an archive is
/// pruned from the record and nothing else refers to them. Snippets come from
/// the compressed copy in chunk.text_z.
/// </summary>
internal static class Schema
{
    public const int Version = 1;

    public const string Core = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE archive(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE,          -- relative to the record root
            kind TEXT NOT NULL,
            archive_date TEXT,                  -- from the file name stamp, else its write time
            size INTEGER NOT NULL,
            mtime_utc TEXT NOT NULL,
            indexed_utc TEXT NOT NULL,
            member_count INTEGER NOT NULL DEFAULT 0,
            text_members INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE body(
            id INTEGER PRIMARY KEY,
            sha256 BLOB NOT NULL UNIQUE,
            size INTEGER NOT NULL,
            chunk_count INTEGER NOT NULL
        );

        CREATE TABLE member(
            id INTEGER PRIMARY KEY,
            archive_id INTEGER NOT NULL REFERENCES archive(id) ON DELETE CASCADE,
            path TEXT NOT NULL,
            crc32 INTEGER,
            size INTEGER NOT NULL,
            mtime TEXT,
            kind TEXT NOT NULL,
            body_id INTEGER REFERENCES body(id),
            note TEXT                           -- why a member has no body: binary, nested archive, too large
        );
        CREATE INDEX member_archive ON member(archive_id);
        CREATE INDEX member_body ON member(body_id);
        CREATE INDEX member_sig ON member(crc32, size);

        CREATE TABLE chunk(
            id INTEGER PRIMARY KEY,
            sha256 BLOB NOT NULL UNIQUE,
            text_z BLOB NOT NULL,               -- Brotli-compressed UTF-8
            line_map BLOB NOT NULL,             -- per source line: text offset delta, raw byte length (varints)
            char_len INTEGER NOT NULL,
            first_ts TEXT                       -- first timestamp seen in the chunk (transcripts)
        );

        CREATE TABLE body_chunk(
            body_id INTEGER NOT NULL REFERENCES body(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            chunk_id INTEGER NOT NULL REFERENCES chunk(id),
            line_start INTEGER NOT NULL,        -- 1-based source line where the chunk begins
            byte_offset INTEGER NOT NULL,       -- byte offset of that line in the member
            PRIMARY KEY(body_id, seq)
        ) WITHOUT ROWID;
        CREATE INDEX body_chunk_chunk ON body_chunk(chunk_id);
        """;

    public const string Fts = """
        CREATE VIRTUAL TABLE chunk_fts USING fts5(
            text,
            content='',
            contentless_delete=1,
            tokenize='porter unicode61 remove_diacritics 2'
        );
        """;
}
