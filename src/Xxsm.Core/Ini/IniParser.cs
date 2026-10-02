using System.Collections.Immutable;
using System.Text;
using Xxsm.Core.Diagnostics;

namespace Xxsm.Core.Ini;

/// <summary>The tolerant 3DMigoto INI parser, following 3DMigoto's own reading rules. Never throws.</summary>
/// <remarks>
/// A comment is a line starting <c>;</c> or <c>//</c>. Neither is a comment mid-line: <c>//</c> is integer division
/// there. A section name runs to the first <c>]</c>; a key and value split at the first <c>=</c>. Duplicate keys and
/// sections are kept, in order.
/// </remarks>
public static class IniParser
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Keys 3DMigoto itself allows before the first section header.</summary>
    private static readonly string[] PreambleKeys = ["namespace", "condition"];

    /// <summary>Parses a file's bytes.</summary>
    /// <param name="bytes">The whole file. May be empty, truncated, or not text at all.</param>
    /// <param name="path">The path it came from, for messages.</param>
    /// <param name="truncated">True when <paramref name="bytes"/> is only the start of a longer file.</param>
    /// <returns>A document that reproduces <paramref name="bytes"/> exactly.</returns>
    public static IniDocument Parse(ReadOnlySpan<byte> bytes, string? path = null, bool truncated = false)
    {
        var owned = bytes.ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var (encoding, bom) = DetectEncoding(owned, diagnostics);
        var decoder = encoding.ToEncoding();

        if (Array.IndexOf(owned, (byte)0) >= 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                IniDiagnosticCodes.NulByte,
                "This file contains a zero byte, which usually means it is not a text file. "
                + "It was read anyway, but the result may be nonsense."));
        }

        if (truncated)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                IniDiagnosticCodes.Truncated,
                "This file was too large to read in full, so only the beginning of it was "
                + "looked at. Anything after that point was not seen."));
        }

        var lines = ImmutableArray.CreateBuilder<IniLine>();
        var sections = ImmutableArray.CreateBuilder<IniSection>();

        var current = new IniSection(0, string.Empty, header: null);
        sections.Add(current);

        var endings = IniLineEndings.None;
        var position = bom.Length;
        var number = 0;

        while (position < owned.Length)
        {
            var contentEnd = position;
            while (contentEnd < owned.Length && owned[contentEnd] is not ((byte)'\n' or (byte)'\r'))
            {
                contentEnd++;
            }

            var terminatorLength = 0;
            if (contentEnd < owned.Length)
            {
                terminatorLength = owned[contentEnd] == (byte)'\r'
                    && contentEnd + 1 < owned.Length
                    && owned[contentEnd + 1] == (byte)'\n'
                        ? 2
                        : 1;
                endings = Combine(endings, terminatorLength == 2 ? IniLineEndings.CrLf : IniLineEndings.Lf);
            }

            number++;
            var line = Classify(
                owned, decoder, number, position, contentEnd, terminatorLength, sections, ref current, diagnostics);
            lines.Add(line);
            current.Add(line);

            position = contentEnd + terminatorLength;
        }

        return new IniDocument(
            owned,
            path,
            encoding,
            bom,
            endings,
            truncated,
            lines.ToImmutable(),
            sections.ToImmutable(),
            diagnostics.ToImmutable());
    }

    /// <summary>Parses text, as UTF-8.</summary>
    /// <param name="text">The INI text.</param>
    /// <param name="path">The path it came from, for messages.</param>
    /// <returns>The document.</returns>
    public static IniDocument ParseText(string text, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(Encoding.UTF8.GetBytes(text), path);
    }

    private static IniLine Classify(
        byte[] bytes,
        Encoding decoder,
        int number,
        int start,
        int end,
        int terminatorLength,
        ImmutableArray<IniSection>.Builder sections,
        ref IniSection current,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var span = new ByteSpan(start, end - start);
        var terminator = new ByteSpan(end, terminatorLength);
        var text = Decode(decoder, bytes, span);

        var first = start;
        while (first < end && IsBlank(bytes[first]))
        {
            first++;
        }

        if (first == end)
        {
            return new IniLine(number, IniLineKind.Blank, span, terminator, text, current.Index);
        }

        var last = end;
        while (last > first && IsBlank(bytes[last - 1]))
        {
            last--;
        }

        if (bytes[first] == (byte)';'
            || (bytes[first] == (byte)'/' && first + 1 < last && bytes[first + 1] == (byte)'/'))
        {
            return new IniLine(number, IniLineKind.Comment, span, terminator, text, current.Index);
        }

        if (bytes[first] == (byte)'[')
        {
            var name = ReadSectionName(bytes, decoder, first, last, number, diagnostics);
            var header = new IniLine(
                number, IniLineKind.SectionHeader, span, terminator, text, sections.Count, sectionName: name);
            current = new IniSection(sections.Count, name, header);
            sections.Add(current);
            return header;
        }

        var equals = IndexOf(bytes, first, last, (byte)'=');
        if (equals < 0)
        {
            return new IniLine(number, IniLineKind.Directive, span, terminator, text, current.Index);
        }

        var keyEnd = equals;
        while (keyEnd > first && IsBlank(bytes[keyEnd - 1]))
        {
            keyEnd--;
        }

        var keySpan = new ByteSpan(first, keyEnd - first);
        var key = Decode(decoder, bytes, keySpan);

        var valueStart = equals + 1;
        while (valueStart < last && IsBlank(bytes[valueStart]))
        {
            valueStart++;
        }

        var valueSpan = valueStart < last ? new ByteSpan(valueStart, last - valueStart) : new ByteSpan(end, 0);
        var value = Decode(decoder, bytes, valueSpan);

        if (keySpan.IsEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                IniDiagnosticCodes.EmptyKey,
                $"This line has a value but no setting name before the '=': \"{text.Trim()}\".",
                number));
        }
        else if (current.IsPreamble && !PreambleKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                IniDiagnosticCodes.EntryOutsideSection,
                $"\"{key}\" appears before any [Section] heading, so 3DMigoto will ignore it.",
                number));
        }

        return new IniLine(
            number, IniLineKind.Entry, span, terminator, text, current.Index,
            key: key, value: value, keySpan: keySpan, valueSpan: valueSpan);
    }

    private static string ReadSectionName(
        byte[] bytes,
        Encoding decoder,
        int first,
        int last,
        int number,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // "[Foo] junk" is section "Foo", as 3DMigoto reads it.
        var close = IndexOf(bytes, first + 1, last, (byte)']');
        var nameEnd = close;
        if (close < 0)
        {
            nameEnd = last;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                IniDiagnosticCodes.UnterminatedSection,
                $"The section heading on this line has no closing ']'. Everything after the "
                + $"'[' was used as its name, which is what 3DMigoto does.",
                number));
        }

        var nameStart = first + 1;
        while (nameStart < nameEnd && IsBlank(bytes[nameStart]))
        {
            nameStart++;
        }

        while (nameEnd > nameStart && IsBlank(bytes[nameEnd - 1]))
        {
            nameEnd--;
        }

        var name = Decode(decoder, bytes, new ByteSpan(nameStart, nameEnd - nameStart));
        if (name.Length == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                IniDiagnosticCodes.EmptySectionName,
                "This section heading has no name in it.",
                number));
        }

        return name;
    }

    private static (IniTextEncoding Encoding, ByteSpan Bom) DetectEncoding(
        byte[] bytes, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Error,
                IniDiagnosticCodes.Utf16ByteOrderMark,
                "This file starts with a UTF-16 byte order mark. 3DMigoto reads INIs one byte "
                + "at a time and cannot read UTF-16, so it will not work in the game either. "
                + "Re-save it as UTF-8."));
        }

        var hasBom = bytes.Length >= 3 && bytes.AsSpan(0, 3).SequenceEqual(Utf8Bom);
        var bom = hasBom ? new ByteSpan(0, 3) : new ByteSpan(0, 0);

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(bytes, bom.Length, bytes.Length - bom.Length);
            return (hasBom ? IniTextEncoding.Utf8WithByteOrderMark : IniTextEncoding.Utf8, bom);
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Info,
                IniDiagnosticCodes.NotUtf8,
                "This file is not valid UTF-8. It was read as Latin-1 instead, one character "
                + "per byte, which is how 3DMigoto reads it too."));
            return (IniTextEncoding.Latin1, bom);
        }
    }

    private static string Decode(Encoding decoder, byte[] bytes, ByteSpan span) =>
        span.IsEmpty ? string.Empty : decoder.GetString(bytes, span.Start, span.Length);

    private static bool IsBlank(byte value) => value is (byte)' ' or (byte)'\t';

    private static int IndexOf(byte[] bytes, int start, int end, byte value)
    {
        for (var i = start; i < end; i++)
        {
            if (bytes[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    private static IniLineEndings Combine(IniLineEndings seen, IniLineEndings found) => seen switch
    {
        IniLineEndings.None => found,
        _ when seen == found => seen,
        _ => IniLineEndings.Mixed,
    };
}
