using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class IssueComment
{
    public long Id { get; set; }

    public long GithubId { get; set; }

    public string? NodeId { get; set; }

    public long RepositoryId { get; set; }

    public long IssueId { get; set; }

    public long? AuthorUserId { get; set; }

    public string? Body { get; set; }

    public string? AuthorAssociation { get; set; }

    public string? HtmlUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual User? AuthorUser { get; set; }

    public virtual Issue Issue { get; set; } = null!;

    public virtual Repository Repository { get; set; } = null!;
}
