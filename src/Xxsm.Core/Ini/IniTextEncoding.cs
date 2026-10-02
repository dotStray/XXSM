using System.Text;

namespace Xxsm.Core.Ini;

/// <summary>How the bytes of an INI file were turned into text: UTF-8, else Latin-1, which cannot fail.</summary>
public enum IniTextEncoding
{
    /// <summary>Valid UTF-8, no byte order mark.</summary>
    Utf8 = 0,

    /// <summary>Valid UTF-8 behind a UTF-8 byte order mark.</summary>
    Utf8WithByteOrderMark,

    /// <summary>Not valid UTF-8. Read as Latin-1, where every byte is a character.</summary>
    Latin1,
}

/// <summary>Helpers for <see cref="IniTextEncoding"/>.</summary>
public static class IniTextEncodingExtensions
{
    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Encoding into Latin-1 can fail; the default fallback would silently write '?' instead.
    private static readonly Encoding StrictLatin1 = Encoding.GetEncoding(
        "ISO-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

    /// <summary>The encoding to write replacement text in, as the file was read; it throws, never mangles.</summary>
    /// <param name="encoding">The encoding the document was read with.</param>
    /// <returns>A throwing encoder.</returns>
    public static Encoding ToEncoding(this IniTextEncoding encoding) => encoding switch
    {
        IniTextEncoding.Latin1 => StrictLatin1,
        _ => Utf8NoBom,
    };
}
