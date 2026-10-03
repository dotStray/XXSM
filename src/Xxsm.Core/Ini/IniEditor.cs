using System.Text;

namespace Xxsm.Core.Ini;

/// <summary>Surgical write: changes the text of values and leaves every other byte of the file alone.</summary>
/// <remarks>New text is encoded as the file was read; text that cannot be encoded so is refused.</remarks>
public static class IniEditor
{
    /// <summary>Replaces one entry's value.</summary>
    /// <param name="document">The document the entry came from.</param>
    /// <param name="entry">The entry line to change. Must be a <see cref="IniLineKind.Entry"/>.</param>
    /// <param name="value">The new value text. May be empty.</param>
    /// <returns>New file bytes, identical to the original apart from that value.</returns>
    /// <exception cref="ArgumentException"><paramref name="entry"/> is not an entry line.</exception>
    /// <exception cref="IniWriteException">The new value cannot be encoded the way the file is.</exception>
    public static byte[] SetValue(IniDocument document, IniLine entry, string value)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Apply(document, [new IniEdit(entry, value)]);
    }

    /// <summary>Replaces the first matching key in the first matching section, as 3DMigoto reads it.</summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="section">The section name, without brackets. Matched ignoring case.</param>
    /// <param name="key">The key. Matched ignoring case.</param>
    /// <param name="value">The new value text.</param>
    /// <param name="bytes">The new file bytes, when the key was found.</param>
    /// <returns><see langword="true"/> if the key was found and replaced.</returns>
    public static bool TrySetValue(
        IniDocument document, string section, string key, string value, out byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(document);

        var entry = document.FindSection(section)?.Find(key);
        if (entry is null)
        {
            bytes = [];
            return false;
        }

        bytes = SetValue(document, entry, value);
        return true;
    }

    /// <summary>Applies several value replacements in one pass.</summary>
    /// <param name="document">The document the entries came from.</param>
    /// <param name="edits">The edits, in any order. A directive line, such as <c>global persist $x</c>, gains
    /// <c> = value</c> after its last non-blank character.</param>
    /// <returns>New file bytes.</returns>
    /// <exception cref="ArgumentException">An edit names a line that is neither an entry nor a directive, or two
    /// edits overlap.</exception>
    /// <exception cref="IniWriteException">A new value cannot be encoded the way the file is.</exception>
    public static byte[] Apply(IniDocument document, IReadOnlyList<IniEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(edits);

        if (edits.Count == 0)
        {
            return document.ToBytes();
        }

        var encoder = document.Encoding.ToEncoding();
        var ordered = new List<(ByteSpan Span, byte[] Replacement)>(edits.Count);

        foreach (var edit in edits)
        {
            if (edit.Entry.Kind == IniLineKind.Directive)
            {
                ordered.Add((EndOfText(document, edit.Entry), Encode(encoder, " = " + edit.Value, document, edit.Entry)));
                continue;
            }

            if (edit.Entry.Kind != IniLineKind.Entry)
            {
                throw new ArgumentException(
                    $"Line {edit.Entry.Number} is a {edit.Entry.Kind} line, not a setting, so it has no value to change.",
                    nameof(edits));
            }

            ordered.Add((edit.Entry.ValueSpan, Encode(encoder, edit.Value, document, edit.Entry)));
        }

        ordered.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));

        for (var i = 1; i < ordered.Count; i++)
        {
            // Equal starts overlap too: two empty values at one place are two zero-length spans.
            if (ordered[i].Span.Start < ordered[i - 1].Span.End ||
                ordered[i].Span.Start == ordered[i - 1].Span.Start)
            {
                throw new ArgumentException("Two edits change overlapping parts of the file.", nameof(edits));
            }
        }

        var source = document.Bytes;
        var grown = ordered.Sum(edit => edit.Replacement.Length - edit.Span.Length);
        var result = new byte[source.Length + grown];

        var read = 0;
        var write = 0;
        foreach (var (span, replacement) in ordered)
        {
            source[read..span.Start].CopyTo(result.AsSpan(write));
            write += span.Start - read;

            replacement.CopyTo(result.AsSpan(write));
            write += replacement.Length;

            read = span.End;
        }

        source[read..].CopyTo(result.AsSpan(write));
        return result;
    }

    private static ByteSpan EndOfText(IniDocument document, IniLine line)
    {
        var bytes = document.Bytes;
        var end = line.Span.End;

        while (end > line.Span.Start && bytes[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return new ByteSpan(end, 0);
    }

    private static byte[] Encode(Encoding encoder, string value, IniDocument document, IniLine entry)
    {
        try
        {
            return encoder.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new IniWriteException(
                $"\"{value}\" cannot be written into this file: it is stored as "
                + $"{document.Encoding} and contains a character that encoding has no room for. "
                + $"The file was not changed.",
                document.Path,
                entry.Number,
                exception);
        }
    }
}

/// <summary>One value replacement for <see cref="IniEditor.Apply"/>.</summary>
/// <param name="Entry">The entry line whose value changes, or a directive line that gains one.</param>
/// <param name="Value">The new value text.</param>
public readonly record struct IniEdit(IniLine Entry, string Value);
