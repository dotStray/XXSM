using Xxsm.Core.Diagnostics;

namespace Xxsm.Core.Ini;

/// <summary>Stable codes for the <see cref="Diagnostic"/>s the INI parser raises.</summary>
public static class IniDiagnosticCodes
{
    /// <summary>A section header had no closing bracket. Everything to end of line was used as the name.</summary>
    public const string UnterminatedSection = "ini.section.unterminated";

    /// <summary>A section header had no name at all — a bare <c>[]</c>.</summary>
    public const string EmptySectionName = "ini.section.empty-name";

    /// <summary>A <c>key = value</c> line appeared before any section header.</summary>
    public const string EntryOutsideSection = "ini.entry.outside-section";

    /// <summary>A line had an <c>=</c> but nothing before it.</summary>
    public const string EmptyKey = "ini.entry.empty-key";

    /// <summary>The file is not valid UTF-8 and was read as Latin-1 instead.</summary>
    public const string NotUtf8 = "ini.encoding.not-utf8";

    /// <summary>The file starts with a UTF-16 byte order mark, which 3DMigoto cannot read.</summary>
    public const string Utf16ByteOrderMark = "ini.encoding.utf16-bom";

    /// <summary>The file was longer than the caller's budget and only its start was read.</summary>
    public const string Truncated = "ini.content.truncated";

    /// <summary>The file contains a NUL byte, which usually means it is not text at all.</summary>
    public const string NulByte = "ini.content.nul-byte";
}
