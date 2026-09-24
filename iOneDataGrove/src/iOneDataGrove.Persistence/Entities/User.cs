using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class User
{
    public long Id { get; set; }

    public long GithubId { get; set; }

    public string? NodeId { get; set; }

    public string Login { get; set; } = null!;

    public string? Name { get; set; }

    public string? Email { get; set; }

    public string? Company { get; set; }

    public string? Location { get; set; }

    public string? AvatarUrl { get; set; }

    public string? HtmlUrl { get; set; }

    public string? UserType { get; set; }

    public bool? SiteAdmin { get; set; }

    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual ICollection<Commit> CommitAuthorUsers { get; set; } = new List<Commit>();

    public virtual ICollection<Commit> CommitCommitterUsers { get; set; } = new List<Commit>();

    public virtual ICollection<Issue> IssueAuthorUsers { get; set; } = new List<Issue>();

    public virtual ICollection<Issue> IssueClosedByUsers { get; set; } = new List<Issue>();

    public virtual ICollection<IssueComment> IssueComments { get; set; } = new List<IssueComment>();

    public virtual ICollection<PullRequest> PullRequestAuthorUsers { get; set; } = new List<PullRequest>();

    public virtual ICollection<PullRequest> PullRequestMergedByUsers { get; set; } = new List<PullRequest>();

    public virtual ICollection<Repository> Repositories { get; set; } = new List<Repository>();
}
