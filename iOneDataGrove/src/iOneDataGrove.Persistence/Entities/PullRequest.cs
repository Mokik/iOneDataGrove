using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class PullRequest
{
    public long Id { get; set; }

    public long GithubId { get; set; }

    public string? NodeId { get; set; }

    public long RepositoryId { get; set; }

    public int Number { get; set; }

    public long? AuthorUserId { get; set; }

    public string Title { get; set; } = null!;

    public string? Body { get; set; }

    public string State { get; set; } = null!;

    public bool IsDraft { get; set; }

    public bool Locked { get; set; }

    public string? BaseBranch { get; set; }

    public string? BaseSha { get; set; }

    public string? HeadBranch { get; set; }

    public string? HeadSha { get; set; }

    public bool Merged { get; set; }

    public DateTime? MergedAt { get; set; }

    public long? MergedByUserId { get; set; }

    public string? MergeCommitSha { get; set; }

    public int? CommitsCount { get; set; }

    public int? Additions { get; set; }

    public int? Deletions { get; set; }

    public int? ChangedFiles { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual User? AuthorUser { get; set; }

    public virtual User? MergedByUser { get; set; }

    public virtual ICollection<PullRequestCommit> PullRequestCommits { get; set; } = new List<PullRequestCommit>();

    public virtual ICollection<PullRequestFile> PullRequestFiles { get; set; } = new List<PullRequestFile>();

    public virtual Repository Repository { get; set; } = null!;
}
