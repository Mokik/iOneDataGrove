using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace iOneDataGrove.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BaselineExistingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "github");

            migrationBuilder.EnsureSchema(
                name: "ingestion");

            migrationBuilder.CreateTable(
                name: "users",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    github_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: true),
                    login = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    company = table.Column<string>(type: "text", nullable: true),
                    location = table.Column<string>(type: "text", nullable: true),
                    avatar_url = table.Column<string>(type: "text", nullable: true),
                    html_url = table.Column<string>(type: "text", nullable: true),
                    user_type = table.Column<string>(type: "text", nullable: true),
                    site_admin = table.Column<bool>(type: "boolean", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("users_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "repositories",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    github_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: true),
                    owner_user_id = table.Column<long>(type: "bigint", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    html_url = table.Column<string>(type: "text", nullable: false),
                    is_private = table.Column<bool>(type: "boolean", nullable: false),
                    is_fork = table.Column<bool>(type: "boolean", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    is_disabled = table.Column<bool>(type: "boolean", nullable: false),
                    default_branch = table.Column<string>(type: "text", nullable: true),
                    primary_language = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    pushed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("repositories_pkey", x => x.id);
                    table.ForeignKey(
                        name: "repositories_owner_user_id_fkey",
                        column: x => x.owner_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "commits",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    sha = table.Column<string>(type: "text", nullable: false),
                    author_user_id = table.Column<long>(type: "bigint", nullable: true),
                    committer_user_id = table.Column<long>(type: "bigint", nullable: true),
                    author_name = table.Column<string>(type: "text", nullable: true),
                    author_email = table.Column<string>(type: "text", nullable: true),
                    committer_name = table.Column<string>(type: "text", nullable: true),
                    committer_email = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    authored_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    committed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    additions = table.Column<int>(type: "integer", nullable: true),
                    deletions = table.Column<int>(type: "integer", nullable: true),
                    files_changed = table.Column<int>(type: "integer", nullable: true),
                    html_url = table.Column<string>(type: "text", nullable: true),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("commits_pkey", x => x.id);
                    table.ForeignKey(
                        name: "commits_author_user_id_fkey",
                        column: x => x.author_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "commits_committer_user_id_fkey",
                        column: x => x.committer_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "commits_repository_id_fkey",
                        column: x => x.repository_id,
                        principalSchema: "github",
                        principalTable: "repositories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "issues",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    github_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: true),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    author_user_id = table.Column<long>(type: "bigint", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    state_reason = table.Column<string>(type: "text", nullable: true),
                    locked = table.Column<bool>(type: "boolean", nullable: false),
                    comments_count = table.Column<int>(type: "integer", nullable: false),
                    html_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("issues_pkey", x => x.id);
                    table.ForeignKey(
                        name: "issues_author_user_id_fkey",
                        column: x => x.author_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "issues_closed_by_user_id_fkey",
                        column: x => x.closed_by_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "issues_repository_id_fkey",
                        column: x => x.repository_id,
                        principalSchema: "github",
                        principalTable: "repositories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "pull_requests",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    github_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: true),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    author_user_id = table.Column<long>(type: "bigint", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    is_draft = table.Column<bool>(type: "boolean", nullable: false),
                    locked = table.Column<bool>(type: "boolean", nullable: false),
                    base_branch = table.Column<string>(type: "text", nullable: true),
                    base_sha = table.Column<string>(type: "text", nullable: true),
                    head_branch = table.Column<string>(type: "text", nullable: true),
                    head_sha = table.Column<string>(type: "text", nullable: true),
                    merged = table.Column<bool>(type: "boolean", nullable: false),
                    merged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    merged_by_user_id = table.Column<long>(type: "bigint", nullable: true),
                    merge_commit_sha = table.Column<string>(type: "text", nullable: true),
                    commits_count = table.Column<int>(type: "integer", nullable: true),
                    additions = table.Column<int>(type: "integer", nullable: true),
                    deletions = table.Column<int>(type: "integer", nullable: true),
                    changed_files = table.Column<int>(type: "integer", nullable: true),
                    html_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pull_requests_pkey", x => x.id);
                    table.ForeignKey(
                        name: "pull_requests_author_user_id_fkey",
                        column: x => x.author_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "pull_requests_merged_by_user_id_fkey",
                        column: x => x.merged_by_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "pull_requests_repository_id_fkey",
                        column: x => x.repository_id,
                        principalSchema: "github",
                        principalTable: "repositories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                schema: "ingestion",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    repository_id = table.Column<long>(type: "bigint", nullable: true),
                    resource_type = table.Column<string>(type: "text", nullable: true),
                    sync_type = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    items_read = table.Column<int>(type: "integer", nullable: false),
                    items_inserted = table.Column<int>(type: "integer", nullable: false),
                    items_updated = table.Column<int>(type: "integer", nullable: false),
                    items_failed = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("sync_runs_pkey", x => x.id);
                    table.ForeignKey(
                        name: "sync_runs_repository_id_fkey",
                        column: x => x.repository_id,
                        principalSchema: "github",
                        principalTable: "repositories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "sync_state",
                schema: "ingestion",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: false),
                    backfill_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    backfill_to = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_successful_sync = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_github_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cursor = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'pending'::text"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("sync_state_pkey", x => x.id);
                    table.ForeignKey(
                        name: "sync_state_repository_id_fkey",
                        column: x => x.repository_id,
                        principalSchema: "github",
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commit_files",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    commit_id = table.Column<long>(type: "bigint", nullable: false),
                    filename = table.Column<string>(type: "text", nullable: false),
                    previous_filename = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    additions = table.Column<int>(type: "integer", nullable: true),
                    deletions = table.Column<int>(type: "integer", nullable: true),
                    changes = table.Column<int>(type: "integer", nullable: true),
                    patch = table.Column<string>(type: "text", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("commit_files_pkey", x => x.id);
                    table.ForeignKey(
                        name: "commit_files_commit_id_fkey",
                        column: x => x.commit_id,
                        principalSchema: "github",
                        principalTable: "commits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issue_comments",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    github_id = table.Column<long>(type: "bigint", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: true),
                    repository_id = table.Column<long>(type: "bigint", nullable: false),
                    issue_id = table.Column<long>(type: "bigint", nullable: false),
                    author_user_id = table.Column<long>(type: "bigint", nullable: true),
                    body = table.Column<string>(type: "text", nullable: true),
                    author_association = table.Column<string>(type: "text", nullable: true),
                    html_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("issue_comments_pkey", x => x.id);
                    table.ForeignKey(
                        name: "issue_comments_author_user_id_fkey",
                        column: x => x.author_user_id,
                        principalSchema: "github",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "issue_comments_issue_id_fkey",
                        column: x => x.issue_id,
                        principalSchema: "github",
                        principalTable: "issues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "issue_comments_repository_id_fkey",
                        column: x => x.repository_id,
                        principalSchema: "github",
                        principalTable: "repositories",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "pull_request_commits",
                schema: "github",
                columns: table => new
                {
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    commit_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pull_request_commits_pkey", x => new { x.pull_request_id, x.commit_id });
                    table.ForeignKey(
                        name: "pull_request_commits_commit_id_fkey",
                        column: x => x.commit_id,
                        principalSchema: "github",
                        principalTable: "commits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "pull_request_commits_pull_request_id_fkey",
                        column: x => x.pull_request_id,
                        principalSchema: "github",
                        principalTable: "pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pull_request_files",
                schema: "github",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    filename = table.Column<string>(type: "text", nullable: false),
                    previous_filename = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: true),
                    additions = table.Column<int>(type: "integer", nullable: true),
                    deletions = table.Column<int>(type: "integer", nullable: true),
                    changes = table.Column<int>(type: "integer", nullable: true),
                    patch = table.Column<string>(type: "text", nullable: true),
                    raw_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pull_request_files_pkey", x => x.id);
                    table.ForeignKey(
                        name: "pull_request_files_pull_request_id_fkey",
                        column: x => x.pull_request_id,
                        principalSchema: "github",
                        principalTable: "pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_github_commit_files_filename",
                schema: "github",
                table: "commit_files",
                column: "filename");

            migrationBuilder.CreateIndex(
                name: "uq_github_commit_files_commit_filename",
                schema: "github",
                table: "commit_files",
                columns: new[] { "commit_id", "filename" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_commits_author_user_id",
                schema: "github",
                table: "commits",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_commits_committer_user_id",
                schema: "github",
                table: "commits",
                column: "committer_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_commits_repository_committed",
                schema: "github",
                table: "commits",
                columns: new[] { "repository_id", "committed_at" });

            migrationBuilder.CreateIndex(
                name: "uq_github_commits_repository_sha",
                schema: "github",
                table: "commits",
                columns: new[] { "repository_id", "sha" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_issue_comments_issue",
                schema: "github",
                table: "issue_comments",
                column: "issue_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_issue_comments_updated",
                schema: "github",
                table: "issue_comments",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "IX_issue_comments_author_user_id",
                schema: "github",
                table: "issue_comments",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_issue_comments_repository_id",
                schema: "github",
                table: "issue_comments",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "uq_github_issue_comments_github_id",
                schema: "github",
                table: "issue_comments",
                column: "github_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_issues_repository",
                schema: "github",
                table: "issues",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_issues_repository_updated",
                schema: "github",
                table: "issues",
                columns: new[] { "repository_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_github_issues_updated_at",
                schema: "github",
                table: "issues",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "IX_issues_author_user_id",
                schema: "github",
                table: "issues",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_issues_closed_by_user_id",
                schema: "github",
                table: "issues",
                column: "closed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "uq_github_issues_github_id",
                schema: "github",
                table: "issues",
                column: "github_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_github_issues_repository_number",
                schema: "github",
                table: "issues",
                columns: new[] { "repository_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pull_request_commits_commit_id",
                schema: "github",
                table: "pull_request_commits",
                column: "commit_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_pr_files_filename",
                schema: "github",
                table: "pull_request_files",
                column: "filename");

            migrationBuilder.CreateIndex(
                name: "uq_github_pr_files_pr_filename",
                schema: "github",
                table: "pull_request_files",
                columns: new[] { "pull_request_id", "filename" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_github_pull_requests_repository",
                schema: "github",
                table: "pull_requests",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_github_pull_requests_updated",
                schema: "github",
                table: "pull_requests",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "IX_pull_requests_author_user_id",
                schema: "github",
                table: "pull_requests",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_pull_requests_merged_by_user_id",
                schema: "github",
                table: "pull_requests",
                column: "merged_by_user_id");

            migrationBuilder.CreateIndex(
                name: "uq_github_pull_requests_github_id",
                schema: "github",
                table: "pull_requests",
                column: "github_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_github_pull_requests_repository_number",
                schema: "github",
                table: "pull_requests",
                columns: new[] { "repository_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_repositories_owner_user_id",
                schema: "github",
                table: "repositories",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "uq_github_repositories_full_name",
                schema: "github",
                table: "repositories",
                column: "full_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_github_repositories_github_id",
                schema: "github",
                table: "repositories",
                column: "github_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_repository_id",
                schema: "ingestion",
                table: "sync_runs",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "uq_sync_state_repository_resource",
                schema: "ingestion",
                table: "sync_state",
                columns: new[] { "repository_id", "resource_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_github_users_github_id",
                schema: "github",
                table: "users",
                column: "github_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commit_files",
                schema: "github");

            migrationBuilder.DropTable(
                name: "issue_comments",
                schema: "github");

            migrationBuilder.DropTable(
                name: "pull_request_commits",
                schema: "github");

            migrationBuilder.DropTable(
                name: "pull_request_files",
                schema: "github");

            migrationBuilder.DropTable(
                name: "sync_runs",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "sync_state",
                schema: "ingestion");

            migrationBuilder.DropTable(
                name: "issues",
                schema: "github");

            migrationBuilder.DropTable(
                name: "commits",
                schema: "github");

            migrationBuilder.DropTable(
                name: "pull_requests",
                schema: "github");

            migrationBuilder.DropTable(
                name: "repositories",
                schema: "github");

            migrationBuilder.DropTable(
                name: "users",
                schema: "github");
        }
    }
}
