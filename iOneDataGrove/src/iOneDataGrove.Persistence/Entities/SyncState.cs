using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class SyncState
{
    public long Id { get; set; }

    public long RepositoryId { get; set; }

    public string ResourceType { get; set; } = null!;

    public DateTime? BackfillFrom { get; set; }

    public DateTime? BackfillTo { get; set; }

    public DateTime? LastSuccessfulSync { get; set; }

    public DateTime? LastGithubUpdatedAt { get; set; }

    public string? Cursor { get; set; }

    public string Status { get; set; } = null!;

    public DateTime UpdatedAt { get; set; }

    public virtual Repository Repository { get; set; } = null!;
}
