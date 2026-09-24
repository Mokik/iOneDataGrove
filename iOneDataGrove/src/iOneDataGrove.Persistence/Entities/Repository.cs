using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class Repository
{
    public long Id { get; set; }

    public long GithubId { get; set; }

    public string? NodeId { get; set; }

    public long? OwnerUserId { get; set; }

    public string Name { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public string? Description { get; set; }

    public string HtmlUrl { get; set; } = null!;

    public bool IsPrivate { get; set; }

    public bool IsFork { get; set; }

    public bool IsArchived { get; set; }

    public bool IsDisabled { get; set; }

    public bool IsSyncEnabled { get; set; }

    public DateTime? SyncDisabledAt { get; set; }

    public bool IsExcluded { get; set; }

    public DateTime? ExcludedAt { get; set; }

    public string? DefaultBranch { get; set; }

    public string? PrimaryLanguage { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? PushedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual ICollection<Commit> Commits { get; set; } = new List<Commit>();

    public virtual ICollection<IssueComment> IssueComments { get; set; } = new List<IssueComment>();

    public virtual ICollection<Issue> Issues { get; set; } = new List<Issue>();

    public virtual User? OwnerUser { get; set; }

    public virtual ICollection<PullRequest> PullRequests { get; set; } = new List<PullRequest>();

    public virtual ICollection<RepositoryFile> RepositoryFiles { get; set; } = new List<RepositoryFile>();

    public virtual ICollection<SyncRun> SyncRuns { get; set; } = new List<SyncRun>();

    public virtual ICollection<SyncState> SyncStates { get; set; } = new List<SyncState>();
}
