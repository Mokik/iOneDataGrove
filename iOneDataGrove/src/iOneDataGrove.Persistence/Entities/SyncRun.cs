using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class SyncRun
{
    public long Id { get; set; }

    public long? RepositoryId { get; set; }

    public string? ResourceType { get; set; }

    public string SyncType { get; set; } = null!;

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string Status { get; set; } = null!;

    public int ItemsRead { get; set; }

    public int ItemsInserted { get; set; }

    public int ItemsUpdated { get; set; }

    public int ItemsFailed { get; set; }

    public string? ErrorMessage { get; set; }

    public string? Metadata { get; set; }

    public virtual Repository? Repository { get; set; }
}
