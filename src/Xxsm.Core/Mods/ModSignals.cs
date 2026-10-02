using Xxsm.Core.Diagnostics;

namespace Xxsm.Core.Mods;

/// <summary>Everything a mod folder says about which character it is for: evidence, not a decision.</summary>
public sealed record ModSignals
{
    /// <summary>The mod folder that was scanned.</summary>
    public required string Root { get; init; }

    /// <summary>Every candidate hash from the INIs and any <c>hash.json</c>, lowercase, unique, sorted.</summary>
    public required IReadOnlyList<string> Hashes { get; init; }

    /// <summary>The <c>[TextureOverride…]</c> and <c>[ShaderOverride…]</c> sections, in reading order.</summary>
    public required IReadOnlyList<ModOverrideSection> OverrideSections { get; init; }

    /// <summary>Base names of the mod's <c>.ib</c>, <c>.buf</c> and <c>.dds</c> files, casing kept.</summary>
    public required IReadOnlyList<string> AssetNames { get; init; }

    /// <summary>The INI files that were read, as paths relative to <see cref="Root"/>.</summary>
    public required IReadOnlyList<string> IniFiles { get; init; }

    /// <summary>The <c>hash.json</c> files the author shipped, relative to <see cref="Root"/>.</summary>
    public required IReadOnlyList<string> HashJsonFiles { get; init; }

    /// <summary>How many files the walk looked at.</summary>
    public required int FilesSeen { get; init; }

    /// <summary>How many bytes of INI text were read.</summary>
    public required long IniBytesRead { get; init; }

    /// <summary>Which bounds stopped the scan short, if any.</summary>
    public required ModScanLimit LimitsReached { get; init; }

    /// <summary>Anything odd found on the way. Never fatal.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>Whether the scan saw the whole folder.</summary>
    public bool IsComplete => LimitsReached == ModScanLimit.None;
}

/// <summary>One <c>[TextureOverride…]</c> or <c>[ShaderOverride…]</c> section found in a mod's INIs.</summary>
/// <param name="Name">The section name as written, without brackets.</param>
/// <param name="Hashes">The well-formed hashes on this section, lowercase.</param>
/// <param name="MatchFirstIndex">The <c>match_first_index</c> value, when present. A tiebreak hint only.</param>
/// <param name="MatchPriority">The <c>match_priority</c> value, when present. A tiebreak hint only.</param>
/// <param name="File">The INI it came from, relative to the mod folder.</param>
/// <param name="Line">The 1-based line of the section header.</param>
public sealed record ModOverrideSection(
    string Name,
    IReadOnlyList<string> Hashes,
    string? MatchFirstIndex,
    string? MatchPriority,
    string File,
    int Line);
