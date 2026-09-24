BEGIN;

CREATE SCHEMA IF NOT EXISTS knowledge;

CREATE TABLE IF NOT EXISTS knowledge.entity_links
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repository_id   BIGINT NOT NULL
                    REFERENCES github.repositories(id) ON DELETE CASCADE,
    source_type     TEXT NOT NULL,
    source_id       BIGINT NOT NULL,
    target_type     TEXT NOT NULL,
    target_id       BIGINT NOT NULL,
    relation_type   TEXT NOT NULL,
    evidence_type   TEXT NOT NULL,
    evidence_key    TEXT NOT NULL,
    evidence_text   TEXT,
    refreshed_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),

    CONSTRAINT ck_entity_links_source_type
        CHECK (source_type IN (
            'issue', 'issue_comment', 'pull_request', 'commit',
            'repository_file', 'code_symbol'
        )),
    CONSTRAINT ck_entity_links_target_type
        CHECK (target_type IN (
            'issue', 'pull_request', 'commit',
            'repository_file', 'code_symbol'
        )),
    CONSTRAINT ck_entity_links_relation_type
        CHECK (relation_type IN (
            'references', 'closes', 'contains_commit',
            'modifies_file', 'declares_symbol'
        )),
    CONSTRAINT ck_entity_links_evidence_type
        CHECK (evidence_type IN ('github_api', 'text_reference', 'structural_index')),
    CONSTRAINT uq_entity_links_evidence
        UNIQUE (
            repository_id, source_type, source_id,
            target_type, target_id, relation_type, evidence_key
        )
);

CREATE INDEX IF NOT EXISTS ix_entity_links_repository_relation
    ON knowledge.entity_links (repository_id, relation_type);

CREATE INDEX IF NOT EXISTS ix_entity_links_source
    ON knowledge.entity_links (repository_id, source_type, source_id);

CREATE INDEX IF NOT EXISTS ix_entity_links_target
    ON knowledge.entity_links (repository_id, target_type, target_id);

COMMIT;
