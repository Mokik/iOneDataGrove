using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class Issue
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

    public string? StateReason { get; set; }

    public bool Locked { get; set; }

    public int CommentsCount { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public long? ClosedByUserId { get; set; }

    public DateTime SyncedAt { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual User? AuthorUser { get; set; }

    public virtual User? ClosedByUser { get; set; }

    public virtual ICollection<IssueComment> IssueComments { get; set; } = new List<IssueComment>();

    public virtual Repository Repository { get; set; } = null!;
}
