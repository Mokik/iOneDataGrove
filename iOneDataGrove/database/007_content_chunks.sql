BEGIN;

CREATE TABLE IF NOT EXISTS knowledge.chunk_sources
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id       BIGINT NOT NULL
                        REFERENCES github.repositories(id) ON DELETE CASCADE,
    source_type         TEXT NOT NULL,
    source_entity_id    BIGINT NOT NULL,
    source_version      TEXT NOT NULL,
    source_hash         TEXT NOT NULL,
    chunker_version     TEXT NOT NULL,
    title               TEXT NOT NULL,
    source_path         TEXT,
    start_line          INTEGER,
    end_line            INTEGER,
    html_url            TEXT,
    indexed_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_chunk_sources_type
        CHECK (source_type IN (
            'issue', 'issue_comment', 'pull_request', 'commit',
            'repository_file', 'code_symbol'
        )),
    CONSTRAINT ck_chunk_sources_hash
        CHECK (length(source_hash) = 64),
    CONSTRAINT ck_chunk_sources_lines
        CHECK (
            (start_line IS NULL AND end_line IS NULL) OR
            (start_line >= 1 AND end_line >= start_line)
        ),
    CONSTRAINT uq_chunk_sources_entity
        UNIQUE (repository_id, source_type, source_entity_id)
);

CREATE TABLE IF NOT EXISTS knowledge.content_chunks
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    chunk_source_id     BIGINT NOT NULL
                        REFERENCES knowledge.chunk_sources(id) ON DELETE CASCADE,
    repository_id       BIGINT NOT NULL
                        REFERENCES github.repositories(id) ON DELETE CASCADE,
    ordinal             INTEGER NOT NULL,
    content             TEXT NOT NULL,
    content_hash        TEXT NOT NULL,
    character_count     INTEGER NOT NULL,
    estimated_tokens    INTEGER NOT NULL,
    start_line          INTEGER,
    end_line            INTEGER,
    metadata            JSONB NOT NULL DEFAULT '{}'::jsonb,
    indexed_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_content_chunks_ordinal
        CHECK (ordinal >= 0),
    CONSTRAINT ck_content_chunks_hash
        CHECK (length(content_hash) = 64),
    CONSTRAINT ck_content_chunks_character_count
        CHECK (character_count > 0),
    CONSTRAINT ck_content_chunks_estimated_tokens
        CHECK (estimated_tokens > 0),
    CONSTRAINT ck_content_chunks_lines
        CHECK (
            (start_line IS NULL AND end_line IS NULL) OR
            (start_line >= 1 AND end_line >= start_line)
        ),
    CONSTRAINT uq_content_chunks_source_ordinal
        UNIQUE (chunk_source_id, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_chunk_sources_repository_type
    ON knowledge.chunk_sources (repository_id, source_type);

CREATE INDEX IF NOT EXISTS ix_chunk_sources_path
    ON knowledge.chunk_sources (repository_id, source_path)
    WHERE source_path IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_content_chunks_repository
    ON knowledge.content_chunks (repository_id, chunk_source_id, ordinal);

CREATE INDEX IF NOT EXISTS ix_content_chunks_full_text
    ON knowledge.content_chunks
    USING GIN (to_tsvector('simple', content));

ANALYZE knowledge.chunk_sources;
ANALYZE knowledge.content_chunks;

COMMIT;
