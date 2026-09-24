BEGIN;

CREATE INDEX IF NOT EXISTS ix_github_repository_files_full_text
    ON github.repository_files
    USING GIN
    (
        (
            setweight(
                to_tsvector('simple', COALESCE(path, '')),
                'A'
            ) ||
            setweight(
                to_tsvector('simple', COALESCE(content, '')),
                'B'
            )
        )
    )
    WHERE NOT is_deleted;

ANALYZE github.repository_files;

COMMIT;
