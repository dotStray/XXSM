namespace Xxsm.Core.Mods;

/// <summary>The limits a mod-folder scan works within. Reaching one stops the scan and says so.</summary>
/// <param name="MaxDepth">How many directory levels below the mod folder to descend.</param>
/// <param name="MaxFiles">How many files to look at in total.</param>
/// <param name="MaxIniBytes">How many bytes of INI text to read in total.</param>
public readonly record struct ModScanBounds(int MaxDepth, int MaxFiles, int MaxIniBytes)
{
    /// <summary>Depth 8, 5 000 files, 20 MB of INI.</summary>
    public static ModScanBounds Default { get; } = new(MaxDepth: 8, MaxFiles: 5_000, MaxIniBytes: 20 * 1024 * 1024);
}

/// <summary>Which scan bounds were reached. None of these is an error.</summary>
[Flags]
public enum ModScanLimit
{
    /// <summary>The whole folder was scanned.</summary>
    None = 0,

    /// <summary>There were directories deeper than <see cref="ModScanBounds.MaxDepth"/>.</summary>
    Depth = 1,

    /// <summary>There were more files than <see cref="ModScanBounds.MaxFiles"/>.</summary>
    FileCount = 2,

    /// <summary>There was more INI text than <see cref="ModScanBounds.MaxIniBytes"/>.</summary>
    IniBytes = 4,
}
