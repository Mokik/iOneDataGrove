BEGIN;

CREATE INDEX IF NOT EXISTS ix_github_repositories_global_search
    ON github.repositories
    USING GIN
    ((
        setweight(to_tsvector('simple', full_name), 'A') ||
        setweight(to_tsvector('simple', COALESCE(description, '')), 'B')
    ));

CREATE INDEX IF NOT EXISTS ix_github_issues_global_search
    ON github.issues
    USING GIN
    ((
        setweight(to_tsvector('simple', title), 'A') ||
        setweight(to_tsvector('simple', COALESCE(body, '')), 'B')
    ));

CREATE INDEX IF NOT EXISTS ix_github_issue_comments_global_search
    ON github.issue_comments
    USING GIN
    (to_tsvector('simple', COALESCE(body, '')));

CREATE INDEX IF NOT EXISTS ix_github_pull_requests_global_search
    ON github.pull_requests
    USING GIN
    ((
        setweight(to_tsvector('simple', title), 'A') ||
        setweight(to_tsvector('simple', COALESCE(body, '')), 'B')
    ));

CREATE INDEX IF NOT EXISTS ix_github_commits_global_search
    ON github.commits
    USING GIN
    ((
        setweight(to_tsvector('simple', sha), 'A') ||
        setweight(to_tsvector('simple', message), 'B') ||
        setweight(to_tsvector('simple', COALESCE(author_name, '')), 'C')
    ));

CREATE INDEX IF NOT EXISTS ix_code_symbols_global_search
    ON knowledge.code_symbols
    USING GIN
    ((
        setweight(to_tsvector('simple', qualified_name), 'A') ||
        setweight(to_tsvector('simple', signature), 'B') ||
        setweight(to_tsvector('simple', kind), 'C')
    ));

ANALYZE github.repositories;
ANALYZE github.issues;
ANALYZE github.issue_comments;
ANALYZE github.pull_requests;
ANALYZE github.commits;
ANALYZE knowledge.code_symbols;

COMMIT;
