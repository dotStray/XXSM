namespace Xxsm.Cli.Output;

/// <summary>The machine-readable payload of <c>xxsm ini show</c>.</summary>
/// <param name="Path">The file that was read.</param>
/// <param name="Bytes">How big it is.</param>
/// <param name="Encoding">How its bytes were decoded.</param>
/// <param name="LineEndings">Which end-of-line convention it uses.</param>
/// <param name="LineCount">How many physical lines it has.</param>
/// <param name="SectionCount">How many sections, not counting the preamble.</param>
/// <param name="EntryCount">How many <c>key = value</c> lines.</param>
/// <param name="DirectiveCount">How many lines inside sections with no <c>=</c> on them.</param>
/// <param name="CommentCount">How many whole-line comments.</param>
/// <param name="RoundTrips">Whether writing the document back reproduces the file exactly.</param>
/// <param name="Sections">Every section.</param>
/// <param name="Diagnostics">Everything the parser noticed.</param>
public sealed record IniReport(
    string Path,
    int Bytes,
    string Encoding,
    string LineEndings,
    int LineCount,
    int SectionCount,
    int EntryCount,
    int DirectiveCount,
    int CommentCount,
    bool RoundTrips,
    IReadOnlyList<IniReportSection> Sections,
    IReadOnlyList<ScanDiagnostic> Diagnostics);

/// <summary>One section in an <see cref="IniReport"/>.</summary>
/// <param name="Name">The section name, without brackets.</param>
/// <param name="Line">The 1-based line of its header.</param>
/// <param name="Entries">Its <c>key = value</c> lines, in order, duplicates included.</param>
public sealed record IniReportSection(string Name, int Line, IReadOnlyList<IniReportEntry> Entries);

/// <summary>One <c>key = value</c> line.</summary>
public sealed record IniReportEntry(string Key, string Value, int Line);

/// <summary>The machine-readable payload of <c>xxsm ini set</c>.</summary>
/// <param name="Path">The file.</param>
/// <param name="Section">The section that was edited.</param>
/// <param name="Key">The key that was edited.</param>
/// <param name="Line">The 1-based line that changed.</param>
/// <param name="OldValue">What was there before.</param>
/// <param name="NewValue">What is there now.</param>
/// <param name="Offset">Byte offset in the file where the replaced value started.</param>
/// <param name="OldByteCount">How many bytes the old value occupied.</param>
/// <param name="NewByteCount">How many bytes the new value occupies.</param>
/// <param name="UntouchedByteCount">How many bytes of the file were copied through unchanged.</param>
/// <param name="Written">False for a dry run.</param>
public sealed record IniSetReport(
    string Path,
    string Section,
    string Key,
    int Line,
    string OldValue,
    string NewValue,
    int Offset,
    int OldByteCount,
    int NewByteCount,
    int UntouchedByteCount,
    bool Written);

/// <summary>The machine-readable payload of <c>xxsm mod signals</c>.</summary>
/// <param name="Root">The mod folder scanned.</param>
/// <param name="FilesSeen">How many files the walk looked at.</param>
/// <param name="IniFilesRead">How many INIs were parsed.</param>
/// <param name="IniBytesRead">How many bytes of INI text were read.</param>
/// <param name="Complete">Whether the whole folder was scanned.</param>
/// <param name="LimitsReached">Which scan bounds stopped it short.</param>
/// <param name="Hashes">Every candidate hash, lowercase and sorted.</param>
/// <param name="OverrideSections">The override sections found.</param>
/// <param name="AssetNames">Base names of the mod's <c>.ib</c>, <c>.buf</c> and <c>.dds</c> files.</param>
/// <param name="IniFiles">The INIs that were read, relative to the mod folder.</param>
/// <param name="HashJsonFiles">Any <c>hash.json</c> the author shipped.</param>
/// <param name="Diagnostics">Everything the scan noticed.</param>
public sealed record ModSignalReport(
    string Root,
    int FilesSeen,
    int IniFilesRead,
    long IniBytesRead,
    bool Complete,
    string LimitsReached,
    IReadOnlyList<string> Hashes,
    IReadOnlyList<ModSignalSection> OverrideSections,
    IReadOnlyList<string> AssetNames,
    IReadOnlyList<string> IniFiles,
    IReadOnlyList<string> HashJsonFiles,
    IReadOnlyList<ScanDiagnostic> Diagnostics);

/// <summary>Which image one mod's thumbnail would come from (<c>xxsm mod preview</c>).</summary>
/// <param name="Name">The mod's display name.</param>
/// <param name="Path">The mod's own folder.</param>
/// <param name="Image">The chosen image, relative to the mod folder, or null when there is none.</param>
/// <param name="Match">How it was chosen, from <c>declared</c> by the mod's metadata down to an
/// <c>unnamed</c> guess.</param>
public sealed record ModPreviewReport(string Name, string Path, string? Image, string? Match);

/// <summary>One override section in a <see cref="ModSignalReport"/>.</summary>
/// <param name="Name">The section name, without brackets.</param>
/// <param name="Hashes">Its well-formed hashes.</param>
/// <param name="MatchFirstIndex">Its <c>match_first_index</c>, when present.</param>
/// <param name="MatchPriority">Its <c>match_priority</c>, when present.</param>
/// <param name="File">Which INI it came from, relative to the mod folder.</param>
/// <param name="Line">The 1-based line of its header.</param>
public sealed record ModSignalSection(
    string Name,
    IReadOnlyList<string> Hashes,
    string? MatchFirstIndex,
    string? MatchPriority,
    string File,
    int Line);
