BEGIN;

ALTER TABLE github.repositories
    ADD COLUMN IF NOT EXISTS is_sync_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS sync_disabled_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS is_excluded BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS excluded_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS ix_github_repositories_import_control
    ON github.repositories (is_excluded, is_sync_enabled);

COMMIT;
