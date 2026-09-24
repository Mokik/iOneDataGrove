namespace iOneDataGrove.Persistence.Entities;

public partial class RepositoryFile
{
    public long Id { get; set; }

    public long RepositoryId { get; set; }

    public string Path { get; set; } = null!;

    public string FileName { get; set; } = null!;

    public string? Extension { get; set; }

    public string? Language { get; set; }

    public string Branch { get; set; } = null!;

    public string BlobSha { get; set; } = null!;

    public long SizeBytes { get; set; }

    public int LineCount { get; set; }

    public string Content { get; set; } = null!;

    public string ContentEncoding { get; set; } = null!;

    public string HtmlUrl { get; set; } = null!;

    public bool IsDeleted { get; set; }

    public DateTime? DeletedAt { get; set; }

    public DateTime SyncedAt { get; set; }

    public virtual Repository Repository { get; set; } = null!;
}
