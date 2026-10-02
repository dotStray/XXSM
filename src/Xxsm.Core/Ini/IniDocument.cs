using System.Collections.Immutable;
using Xxsm.Core.Diagnostics;

namespace Xxsm.Core.Ini;

/// <summary>A parsed 3DMigoto INI file that keeps every byte it was read from. Parsing never fails.</summary>
public sealed class IniDocument
{
    private readonly byte[] _bytes;

    internal IniDocument(
        byte[] bytes,
        string? path,
        IniTextEncoding encoding,
        ByteSpan byteOrderMark,
        IniLineEndings lineEndings,
        bool truncated,
        ImmutableArray<IniLine> lines,
        ImmutableArray<IniSection> sections,
        ImmutableArray<Diagnostic> diagnostics)
    {
        _bytes = bytes;
        Path = path;
        Encoding = encoding;
        ByteOrderMark = byteOrderMark;
        LineEndings = lineEndings;
        Truncated = truncated;
        Lines = lines;
        Sections = sections;
        Diagnostics = diagnostics;
    }

    /// <summary>The file this was read from, when it came from a file.</summary>
    public string? Path { get; }

    /// <summary>How the bytes were decoded.</summary>
    public IniTextEncoding Encoding { get; }

    /// <summary>The byte order mark, if any; it belongs to no line.</summary>
    public ByteSpan ByteOrderMark { get; }

    /// <summary>Which end-of-line convention the file uses.</summary>
    public IniLineEndings LineEndings { get; }

    /// <summary>Whether the file was cut short at the byte budget; such a document must not be written back.</summary>
    public bool Truncated { get; }

    /// <summary>Every physical line, in order. Nothing is dropped.</summary>
    public ImmutableArray<IniLine> Lines { get; }

    /// <summary>Every section, in order. Index 0 is always the preamble, even when empty.</summary>
    public ImmutableArray<IniSection> Sections { get; }

    /// <summary>Everything the parser noticed. Empty for a well-formed file.</summary>
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>The original bytes, unchanged.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>How many bytes the file was.</summary>
    public int Length => _bytes.Length;

    /// <summary>The original bytes as a new array.</summary>
    /// <returns>A copy of exactly what was parsed.</returns>
    public byte[] ToBytes() => (byte[])_bytes.Clone();

    /// <summary>The bytes a span covers, as a view over the document.</summary>
    /// <param name="span">The span to read.</param>
    /// <returns>The bytes.</returns>
    public ReadOnlySpan<byte> Slice(ByteSpan span) => span.Slice(_bytes);

    /// <summary>Every section with this name, ignoring case, in file order; names may repeat.</summary>
    /// <param name="name">The section name, without brackets.</param>
    /// <returns>The matching sections.</returns>
    public IEnumerable<IniSection> SectionsNamed(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Sections.Where(section =>
            !section.IsPreamble && string.Equals(section.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every section whose name starts with <paramref name="prefix"/>, ignoring case, in file order.</summary>
    /// <param name="prefix">The prefix to match.</param>
    /// <returns>The matching sections.</returns>
    public IEnumerable<IniSection> SectionsStartingWith(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return Sections.Where(section => !section.IsPreamble && section.NameStartsWith(prefix));
    }

    /// <summary>The first section with this name, or null.</summary>
    /// <param name="name">The section name, without brackets.</param>
    /// <returns>The section, or null when the file has no such section.</returns>
    public IniSection? FindSection(string name) => SectionsNamed(name).FirstOrDefault();
}
