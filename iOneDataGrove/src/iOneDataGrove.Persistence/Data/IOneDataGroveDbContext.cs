using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using iOneDataGrove.Persistence.Entities;

namespace iOneDataGrove.Persistence.Data;

public partial class IOneDataGroveDbContext : DbContext
{
    public IOneDataGroveDbContext(DbContextOptions<IOneDataGroveDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Commit> Commits { get; set; }

    public virtual DbSet<CommitFile> CommitFiles { get; set; }

    public virtual DbSet<Issue> Issues { get; set; }

    public virtual DbSet<IssueComment> IssueComments { get; set; }

    public virtual DbSet<PullRequest> PullRequests { get; set; }

    public virtual DbSet<PullRequestCommit> PullRequestCommits { get; set; }

    public virtual DbSet<PullRequestFile> PullRequestFiles { get; set; }

    public virtual DbSet<Repository> Repositories { get; set; }

    public virtual DbSet<RepositoryFile> RepositoryFiles { get; set; }

    public virtual DbSet<SyncRun> SyncRuns { get; set; }

    public virtual DbSet<SyncState> SyncStates { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        NormalizePostgreSqlStrings();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        NormalizePostgreSqlStrings();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void NormalizePostgreSqlStrings()
    {
        foreach (var entry in ChangeTracker.Entries()
                     .Where(item => item.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var property in entry.Properties.Where(item => item.CurrentValue is string))
            {
                var value = (string)property.CurrentValue!;
                property.CurrentValue = property.Metadata.GetColumnType() == "jsonb"
                    ? PostgreSqlValueSanitizer.NormalizeJson(value)
                    : PostgreSqlValueSanitizer.NormalizeText(value);
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Commit>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("commits_pkey");

            entity.ToTable("commits", "github");

            entity.HasIndex(e => new { e.RepositoryId, e.CommittedAt }, "ix_github_commits_repository_committed");

            entity.HasIndex(e => new { e.RepositoryId, e.Sha }, "uq_github_commits_repository_sha").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.Additions).HasColumnName("additions");
            entity.Property(e => e.AuthorEmail).HasColumnName("author_email");
            entity.Property(e => e.AuthorName).HasColumnName("author_name");
            entity.Property(e => e.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(e => e.AuthoredAt).HasColumnName("authored_at");
            entity.Property(e => e.CommittedAt).HasColumnName("committed_at");
            entity.Property(e => e.CommitterEmail).HasColumnName("committer_email");
            entity.Property(e => e.CommitterName).HasColumnName("committer_name");
            entity.Property(e => e.CommitterUserId).HasColumnName("committer_user_id");
            entity.Property(e => e.Deletions).HasColumnName("deletions");
            entity.Property(e => e.FilesChanged).HasColumnName("files_changed");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.Sha).HasColumnName("sha");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");

            entity.HasOne(d => d.AuthorUser).WithMany(p => p.CommitAuthorUsers)
                .HasForeignKey(d => d.AuthorUserId)
                .HasConstraintName("commits_author_user_id_fkey");

            entity.HasOne(d => d.CommitterUser).WithMany(p => p.CommitCommitterUsers)
                .HasForeignKey(d => d.CommitterUserId)
                .HasConstraintName("commits_committer_user_id_fkey");

            entity.HasOne(d => d.Repository).WithMany(p => p.Commits)
                .HasForeignKey(d => d.RepositoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("commits_repository_id_fkey");
        });

        modelBuilder.Entity<CommitFile>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("commit_files_pkey");

            entity.ToTable("commit_files", "github");

            entity.HasIndex(e => e.Filename, "ix_github_commit_files_filename");

            entity.HasIndex(e => new { e.CommitId, e.Filename }, "uq_github_commit_files_commit_filename").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.Additions).HasColumnName("additions");
            entity.Property(e => e.Changes).HasColumnName("changes");
            entity.Property(e => e.CommitId).HasColumnName("commit_id");
            entity.Property(e => e.Deletions).HasColumnName("deletions");
            entity.Property(e => e.Filename).HasColumnName("filename");
            entity.Property(e => e.Patch).HasColumnName("patch");
            entity.Property(e => e.PreviousFilename).HasColumnName("previous_filename");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.Status).HasColumnName("status");

            entity.HasOne(d => d.Commit).WithMany(p => p.CommitFiles)
                .HasForeignKey(d => d.CommitId)
                .HasConstraintName("commit_files_commit_id_fkey");
        });

        modelBuilder.Entity<Issue>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("issues_pkey");

            entity.ToTable("issues", "github");

            entity.HasIndex(e => e.RepositoryId, "ix_github_issues_repository");

            entity.HasIndex(e => new { e.RepositoryId, e.UpdatedAt }, "ix_github_issues_repository_updated");

            entity.HasIndex(e => e.UpdatedAt, "ix_github_issues_updated_at");

            entity.HasIndex(e => e.GithubId, "uq_github_issues_github_id").IsUnique();

            entity.HasIndex(e => new { e.RepositoryId, e.Number }, "uq_github_issues_repository_number").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
            entity.Property(e => e.ClosedByUserId).HasColumnName("closed_by_user_id");
            entity.Property(e => e.CommentsCount).HasColumnName("comments_count");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.GithubId).HasColumnName("github_id");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.Locked).HasColumnName("locked");
            entity.Property(e => e.NodeId).HasColumnName("node_id");
            entity.Property(e => e.Number).HasColumnName("number");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.StateReason).HasColumnName("state_reason");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(d => d.AuthorUser).WithMany(p => p.IssueAuthorUsers)
                .HasForeignKey(d => d.AuthorUserId)
                .HasConstraintName("issues_author_user_id_fkey");

            entity.HasOne(d => d.ClosedByUser).WithMany(p => p.IssueClosedByUsers)
                .HasForeignKey(d => d.ClosedByUserId)
                .HasConstraintName("issues_closed_by_user_id_fkey");

            entity.HasOne(d => d.Repository).WithMany(p => p.Issues)
                .HasForeignKey(d => d.RepositoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("issues_repository_id_fkey");
        });

        modelBuilder.Entity<IssueComment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("issue_comments_pkey");

            entity.ToTable("issue_comments", "github");

            entity.HasIndex(e => e.IssueId, "ix_github_issue_comments_issue");

            entity.HasIndex(e => e.UpdatedAt, "ix_github_issue_comments_updated");

            entity.HasIndex(e => e.GithubId, "uq_github_issue_comments_github_id").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.AuthorAssociation).HasColumnName("author_association");
            entity.Property(e => e.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.GithubId).HasColumnName("github_id");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.IssueId).HasColumnName("issue_id");
            entity.Property(e => e.NodeId).HasColumnName("node_id");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(d => d.AuthorUser).WithMany(p => p.IssueComments)
                .HasForeignKey(d => d.AuthorUserId)
                .HasConstraintName("issue_comments_author_user_id_fkey");

            entity.HasOne(d => d.Issue).WithMany(p => p.IssueComments)
                .HasForeignKey(d => d.IssueId)
                .HasConstraintName("issue_comments_issue_id_fkey");

            entity.HasOne(d => d.Repository).WithMany(p => p.IssueComments)
                .HasForeignKey(d => d.RepositoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("issue_comments_repository_id_fkey");
        });

        modelBuilder.Entity<PullRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pull_requests_pkey");

            entity.ToTable("pull_requests", "github");

            entity.HasIndex(e => e.RepositoryId, "ix_github_pull_requests_repository");

            entity.HasIndex(e => e.UpdatedAt, "ix_github_pull_requests_updated");

            entity.HasIndex(e => e.GithubId, "uq_github_pull_requests_github_id").IsUnique();

            entity.HasIndex(e => new { e.RepositoryId, e.Number }, "uq_github_pull_requests_repository_number").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.Additions).HasColumnName("additions");
            entity.Property(e => e.AuthorUserId).HasColumnName("author_user_id");
            entity.Property(e => e.BaseBranch).HasColumnName("base_branch");
            entity.Property(e => e.BaseSha).HasColumnName("base_sha");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.ChangedFiles).HasColumnName("changed_files");
            entity.Property(e => e.ClosedAt).HasColumnName("closed_at");
            entity.Property(e => e.CommitsCount).HasColumnName("commits_count");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.Deletions).HasColumnName("deletions");
            entity.Property(e => e.GithubId).HasColumnName("github_id");
            entity.Property(e => e.HeadBranch).HasColumnName("head_branch");
            entity.Property(e => e.HeadSha).HasColumnName("head_sha");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.IsDraft).HasColumnName("is_draft");
            entity.Property(e => e.Locked).HasColumnName("locked");
            entity.Property(e => e.MergeCommitSha).HasColumnName("merge_commit_sha");
            entity.Property(e => e.Merged).HasColumnName("merged");
            entity.Property(e => e.MergedAt).HasColumnName("merged_at");
            entity.Property(e => e.MergedByUserId).HasColumnName("merged_by_user_id");
            entity.Property(e => e.NodeId).HasColumnName("node_id");
            entity.Property(e => e.Number).HasColumnName("number");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.State).HasColumnName("state");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");
            entity.Property(e => e.Title).HasColumnName("title");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(d => d.AuthorUser).WithMany(p => p.PullRequestAuthorUsers)
                .HasForeignKey(d => d.AuthorUserId)
                .HasConstraintName("pull_requests_author_user_id_fkey");

            entity.HasOne(d => d.MergedByUser).WithMany(p => p.PullRequestMergedByUsers)
                .HasForeignKey(d => d.MergedByUserId)
                .HasConstraintName("pull_requests_merged_by_user_id_fkey");

            entity.HasOne(d => d.Repository).WithMany(p => p.PullRequests)
                .HasForeignKey(d => d.RepositoryId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("pull_requests_repository_id_fkey");
        });

        modelBuilder.Entity<PullRequestCommit>(entity =>
        {
            entity.HasKey(e => new { e.PullRequestId, e.CommitId }).HasName("pull_request_commits_pkey");

            entity.ToTable("pull_request_commits", "github");

            entity.Property(e => e.PullRequestId).HasColumnName("pull_request_id");
            entity.Property(e => e.CommitId).HasColumnName("commit_id");
            entity.Property(e => e.Position).HasColumnName("position");

            entity.HasOne(d => d.Commit).WithMany(p => p.PullRequestCommits)
                .HasForeignKey(d => d.CommitId)
                .HasConstraintName("pull_request_commits_commit_id_fkey");

            entity.HasOne(d => d.PullRequest).WithMany(p => p.PullRequestCommits)
                .HasForeignKey(d => d.PullRequestId)
                .HasConstraintName("pull_request_commits_pull_request_id_fkey");
        });

        modelBuilder.Entity<PullRequestFile>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("pull_request_files_pkey");

            entity.ToTable("pull_request_files", "github");

            entity.HasIndex(e => e.Filename, "ix_github_pr_files_filename");

            entity.HasIndex(e => new { e.PullRequestId, e.Filename }, "uq_github_pr_files_pr_filename").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.Additions).HasColumnName("additions");
            entity.Property(e => e.Changes).HasColumnName("changes");
            entity.Property(e => e.Deletions).HasColumnName("deletions");
            entity.Property(e => e.Filename).HasColumnName("filename");
            entity.Property(e => e.Patch).HasColumnName("patch");
            entity.Property(e => e.PreviousFilename).HasColumnName("previous_filename");
            entity.Property(e => e.PullRequestId).HasColumnName("pull_request_id");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.Status).HasColumnName("status");

            entity.HasOne(d => d.PullRequest).WithMany(p => p.PullRequestFiles)
                .HasForeignKey(d => d.PullRequestId)
                .HasConstraintName("pull_request_files_pull_request_id_fkey");
        });

        modelBuilder.Entity<Repository>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("repositories_pkey");

            entity.ToTable("repositories", "github");

            entity.HasIndex(e => e.FullName, "uq_github_repositories_full_name").IsUnique();

            entity.HasIndex(e => e.GithubId, "uq_github_repositories_github_id").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.DefaultBranch).HasColumnName("default_branch");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.GithubId).HasColumnName("github_id");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.IsArchived).HasColumnName("is_archived");
            entity.Property(e => e.IsDisabled).HasColumnName("is_disabled");
            entity.Property(e => e.IsSyncEnabled)
                .HasDefaultValue(true)
                .HasColumnName("is_sync_enabled");
            entity.Property(e => e.SyncDisabledAt).HasColumnName("sync_disabled_at");
            entity.Property(e => e.IsExcluded)
                .HasDefaultValue(false)
                .HasColumnName("is_excluded");
            entity.Property(e => e.ExcludedAt).HasColumnName("excluded_at");
            entity.Property(e => e.IsFork).HasColumnName("is_fork");
            entity.Property(e => e.IsPrivate).HasColumnName("is_private");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.NodeId).HasColumnName("node_id");
            entity.Property(e => e.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(e => e.PrimaryLanguage).HasColumnName("primary_language");
            entity.Property(e => e.PushedAt).HasColumnName("pushed_at");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");

            entity.HasOne(d => d.OwnerUser).WithMany(p => p.Repositories)
                .HasForeignKey(d => d.OwnerUserId)
                .HasConstraintName("repositories_owner_user_id_fkey");
        });

        modelBuilder.Entity<RepositoryFile>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("repository_files_pkey");

            entity.ToTable("repository_files", "github");

            entity.HasIndex(e => new { e.RepositoryId, e.IsDeleted }, "ix_github_repository_files_repository_active");

            entity.HasIndex(e => new { e.RepositoryId, e.BlobSha }, "ix_github_repository_files_repository_sha");

            entity.HasIndex(e => new { e.RepositoryId, e.Path }, "uq_github_repository_files_repository_path").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.BlobSha).HasColumnName("blob_sha");
            entity.Property(e => e.Branch).HasColumnName("branch");
            entity.Property(e => e.Content).HasColumnName("content");
            entity.Property(e => e.ContentEncoding)
                .HasDefaultValueSql("'utf-8'::text")
                .HasColumnName("content_encoding");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.Extension).HasColumnName("extension");
            entity.Property(e => e.FileName).HasColumnName("file_name");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.IsDeleted)
                .HasDefaultValue(false)
                .HasColumnName("is_deleted");
            entity.Property(e => e.Language).HasColumnName("language");
            entity.Property(e => e.LineCount).HasColumnName("line_count");
            entity.Property(e => e.Path).HasColumnName("path");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");

            entity.HasOne(d => d.Repository).WithMany(p => p.RepositoryFiles)
                .HasForeignKey(d => d.RepositoryId)
                .HasConstraintName("repository_files_repository_id_fkey");
        });

        modelBuilder.Entity<SyncRun>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sync_runs_pkey");

            entity.ToTable("sync_runs", "ingestion");

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.ItemsFailed).HasColumnName("items_failed");
            entity.Property(e => e.ItemsInserted).HasColumnName("items_inserted");
            entity.Property(e => e.ItemsRead).HasColumnName("items_read");
            entity.Property(e => e.ItemsUpdated).HasColumnName("items_updated");
            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasColumnName("metadata");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.ResourceType).HasColumnName("resource_type");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("started_at");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.SyncType).HasColumnName("sync_type");

            entity.HasOne(d => d.Repository).WithMany(p => p.SyncRuns)
                .HasForeignKey(d => d.RepositoryId)
                .HasConstraintName("sync_runs_repository_id_fkey");
        });

        modelBuilder.Entity<SyncState>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sync_state_pkey");

            entity.ToTable("sync_state", "ingestion");

            entity.HasIndex(e => new { e.RepositoryId, e.ResourceType }, "uq_sync_state_repository_resource").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.BackfillFrom).HasColumnName("backfill_from");
            entity.Property(e => e.BackfillTo).HasColumnName("backfill_to");
            entity.Property(e => e.Cursor).HasColumnName("cursor");
            entity.Property(e => e.LastGithubUpdatedAt).HasColumnName("last_github_updated_at");
            entity.Property(e => e.LastSuccessfulSync).HasColumnName("last_successful_sync");
            entity.Property(e => e.RepositoryId).HasColumnName("repository_id");
            entity.Property(e => e.ResourceType).HasColumnName("resource_type");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'pending'::text")
                .HasColumnName("status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Repository).WithMany(p => p.SyncStates)
                .HasForeignKey(d => d.RepositoryId)
                .HasConstraintName("sync_state_repository_id_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.ToTable("users", "github");

            entity.HasIndex(e => e.GithubId, "uq_github_users_github_id").IsUnique();

            entity.Property(e => e.Id)
                .UseIdentityAlwaysColumn()
                .HasColumnName("id");
            entity.Property(e => e.AvatarUrl).HasColumnName("avatar_url");
            entity.Property(e => e.Company).HasColumnName("company");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.GithubId).HasColumnName("github_id");
            entity.Property(e => e.HtmlUrl).HasColumnName("html_url");
            entity.Property(e => e.Location).HasColumnName("location");
            entity.Property(e => e.Login).HasColumnName("login");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.NodeId).HasColumnName("node_id");
            entity.Property(e => e.RawJson)
                .HasColumnType("jsonb")
                .HasColumnName("raw_json");
            entity.Property(e => e.SiteAdmin).HasColumnName("site_admin");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            entity.Property(e => e.UserType).HasColumnName("user_type");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
