using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class PullRequestCommit
{
    public long PullRequestId { get; set; }

    public long CommitId { get; set; }

    public int? Position { get; set; }

    public virtual Commit Commit { get; set; } = null!;

    public virtual PullRequest PullRequest { get; set; } = null!;
}
