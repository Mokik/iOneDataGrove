using System;
using System.Collections.Generic;

namespace iOneDataGrove.Persistence.Entities;

public partial class CommitFile
{
    public long Id { get; set; }

    public long CommitId { get; set; }

    public string Filename { get; set; } = null!;

    public string? PreviousFilename { get; set; }

    public string? Status { get; set; }

    public int? Additions { get; set; }

    public int? Deletions { get; set; }

    public int? Changes { get; set; }

    public string? Patch { get; set; }

    public string RawJson { get; set; } = null!;

    public virtual Commit Commit { get; set; } = null!;
}
