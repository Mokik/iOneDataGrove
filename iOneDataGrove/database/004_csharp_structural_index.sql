BEGIN;

CREATE SCHEMA IF NOT EXISTS knowledge;

CREATE TABLE IF NOT EXISTS knowledge.code_file_indexes
(
    repository_file_id  BIGINT PRIMARY KEY
                        REFERENCES github.repository_files(id) ON DELETE CASCADE,
    repository_id       BIGINT NOT NULL
                        REFERENCES github.repositories(id) ON DELETE CASCADE,
    blob_sha            TEXT NOT NULL,
    parser_version      TEXT NOT NULL,
    status              TEXT NOT NULL,
    symbol_count        INTEGER NOT NULL DEFAULT 0,
    syntax_error_count  INTEGER NOT NULL DEFAULT 0,
    indexed_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_code_file_indexes_status
        CHECK (status IN ('indexed', 'partial')),
    CONSTRAINT ck_code_file_indexes_symbol_count
        CHECK (symbol_count >= 0),
    CONSTRAINT ck_code_file_indexes_syntax_error_count
        CHECK (syntax_error_count >= 0),
    CONSTRAINT uq_code_file_indexes_file_repository
        UNIQUE (repository_file_id, repository_id)
);

CREATE TABLE IF NOT EXISTS knowledge.code_symbols
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_file_id  BIGINT NOT NULL,
    repository_id       BIGINT NOT NULL,
    kind                TEXT NOT NULL,
    name                TEXT NOT NULL,
    qualified_name      TEXT NOT NULL,
    signature           TEXT NOT NULL,
    containing_symbol   TEXT,
    accessibility       TEXT,
    modifiers           TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
    return_type         TEXT,
    start_line          INTEGER NOT NULL,
    end_line            INTEGER NOT NULL,
    indexed_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_code_symbols_file_index
        FOREIGN KEY (repository_file_id, repository_id)
        REFERENCES knowledge.code_file_indexes(repository_file_id, repository_id)
        ON DELETE CASCADE,
    CONSTRAINT ck_code_symbols_kind
        CHECK (kind IN (
            'namespace', 'class', 'interface', 'enum', 'struct', 'record',
            'method', 'constructor', 'property'
        )),
    CONSTRAINT ck_code_symbols_lines
        CHECK (start_line >= 1 AND end_line >= start_line),
    CONSTRAINT uq_code_symbols_declaration
        UNIQUE (repository_file_id, kind, qualified_name, signature, start_line)
);

CREATE INDEX IF NOT EXISTS ix_code_file_indexes_repository
    ON knowledge.code_file_indexes (repository_id, status, indexed_at DESC);

CREATE INDEX IF NOT EXISTS ix_code_symbols_repository_kind
    ON knowledge.code_symbols (repository_id, kind);

CREATE INDEX IF NOT EXISTS ix_code_symbols_repository_name
    ON knowledge.code_symbols (repository_id, LOWER(name));

CREATE INDEX IF NOT EXISTS ix_code_symbols_file_line
    ON knowledge.code_symbols (repository_file_id, start_line);

COMMIT;
