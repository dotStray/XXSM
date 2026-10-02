namespace Xxsm.Core.Archives;

/// <summary>Limits an extraction works within, so a hostile archive ends in a sentence, not a full disk.</summary>
public sealed record ArchiveBounds
{
    /// <summary>The limits used when a caller does not say.</summary>
    public static ArchiveBounds Default { get; } = new();

    /// <summary>How many entries an archive may hold.</summary>
    public int MaxEntries { get; init; } = 50_000;

    /// <summary>How many bytes may be written in total, across every entry.</summary>
    public long MaxTotalBytes { get; init; } = 16L * 1024 * 1024 * 1024;

    /// <summary>How many bytes one entry may write.</summary>
    public long MaxEntryBytes { get; init; } = 4L * 1024 * 1024 * 1024;
}

/// <summary>Which bound stopped an extraction, if any.</summary>
[Flags]
public enum ArchiveLimit
{
    /// <summary>The whole archive was extracted.</summary>
    None = 0,

    /// <summary><see cref="ArchiveBounds.MaxEntries"/> was reached.</summary>
    Entries = 1,

    /// <summary><see cref="ArchiveBounds.MaxTotalBytes"/> was reached.</summary>
    TotalBytes = 2,

    /// <summary>At least one entry was larger than <see cref="ArchiveBounds.MaxEntryBytes"/>.</summary>
    EntryBytes = 4,
}
