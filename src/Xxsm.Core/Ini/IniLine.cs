namespace Xxsm.Core.Ini;

/// <summary>One physical line of an INI file, classified and kept; the lines reproduce the file exactly.</summary>
public sealed class IniLine
{
    internal IniLine(
        int number,
        IniLineKind kind,
        ByteSpan span,
        ByteSpan terminator,
        string text,
        int sectionIndex,
        string? sectionName = null,
        string? key = null,
        string? value = null,
        ByteSpan keySpan = default,
        ByteSpan valueSpan = default)
    {
        Number = number;
        Kind = kind;
        Span = span;
        Terminator = terminator;
        Text = text;
        SectionIndex = sectionIndex;
        SectionName = sectionName;
        Key = key;
        Value = value;
        KeySpan = keySpan;
        ValueSpan = valueSpan;
    }

    /// <summary>The 1-based line number, counting every line including blanks.</summary>
    public int Number { get; }

    /// <summary>What this line turned out to be.</summary>
    public IniLineKind Kind { get; }

    /// <summary>The line's bytes, not including its end-of-line characters.</summary>
    public ByteSpan Span { get; }

    /// <summary>The end-of-line bytes: <c>\r\n</c>, <c>\n</c> or <c>\r</c>; empty on an unended last line.</summary>
    public ByteSpan Terminator { get; }

    /// <summary>The line as text, decoded but otherwise verbatim, whitespace included.</summary>
    public string Text { get; }

    /// <summary>Index into <see cref="IniDocument.Sections"/> of its section; 0 before the first header.</summary>
    public int SectionIndex { get; }

    /// <summary>For a <see cref="IniLineKind.SectionHeader"/>, the trimmed section name; null otherwise.</summary>
    public string? SectionName { get; }

    /// <summary>For an entry, the text before the first <c>=</c>, trimmed; null otherwise.</summary>
    public string? Key { get; }

    /// <summary>For an entry, the text after the first <c>=</c>, trimmed; never null.</summary>
    public string? Value { get; }

    /// <summary>For an entry, the bytes <see cref="Key"/> was decoded from.</summary>
    public ByteSpan KeySpan { get; }

    /// <summary>For an entry, the bytes <see cref="Value"/> came from: what a surgical write replaces.</summary>
    /// <remarks>An empty value is a zero-length point at the end of the line, so a write appends after the
    /// <c>=</c>.</remarks>
    public ByteSpan ValueSpan { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Number}: {Kind} {Text}";
}
