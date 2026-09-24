using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class Commit
{
    public long Id { get; set; }

    public long RepositoryId { get; set; }

    public string Sha { get; set; } = null!;

    public long? AuthorUserId { get; set; }

    public long? CommitterUserId { get; set; }

    public string? AuthorName { get; set; }

    public string? AuthorEmail { get; set; }

    public string? CommitterName { get; set; }

    public string? CommitterEmail { get; set; }

    public string Message { get; set; } = null!;

    public DateTime? AuthoredAt { get; set; }

    public DateTime? CommittedAt { get; set; }

    public int? Additions { get; set; }

    public int? Deletions { get; set; }

    public int? FilesChanged { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTime SyncedAt { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual User? AuthorUser { get; set; }

    public virtual ICollection<CommitFile> CommitFiles { get; set; } = new List<CommitFile>();

    public virtual User? CommitterUser { get; set; }

    public virtual ICollection<PullRequestCommit> PullRequestCommits { get; set; } = new List<PullRequestCommit>();

    public virtual Repository Repository { get; set; } = null!;
}
