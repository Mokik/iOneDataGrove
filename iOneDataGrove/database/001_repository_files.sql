BEGIN;

CREATE TABLE IF NOT EXISTS github.repository_files
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id       BIGINT NOT NULL
                        REFERENCES github.repositories(id) ON DELETE CASCADE,
    path                TEXT NOT NULL,
    file_name           TEXT NOT NULL,
    extension           TEXT,
    language            TEXT,
    branch              TEXT NOT NULL,
    blob_sha            TEXT NOT NULL,
    size_bytes          BIGINT NOT NULL,
    line_count          INTEGER NOT NULL,
    content             TEXT NOT NULL,
    content_encoding    TEXT NOT NULL DEFAULT 'utf-8',
    html_url            TEXT NOT NULL,
    is_deleted          BOOLEAN NOT NULL DEFAULT FALSE,
    deleted_at          TIMESTAMPTZ,
    synced_at           TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_repository_files_size_non_negative CHECK (size_bytes >= 0),
    CONSTRAINT ck_repository_files_lines_non_negative CHECK (line_count >= 0),
    CONSTRAINT uq_github_repository_files_repository_path UNIQUE (repository_id, path)
);

CREATE INDEX IF NOT EXISTS ix_github_repository_files_repository_active
    ON github.repository_files (repository_id, is_deleted);

CREATE INDEX IF NOT EXISTS ix_github_repository_files_repository_sha
    ON github.repository_files (repository_id, blob_sha);

COMMIT;
