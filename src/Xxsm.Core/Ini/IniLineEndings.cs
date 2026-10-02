namespace Xxsm.Core.Ini;

/// <summary>Which end-of-line convention a file uses. Recorded, never normalised.</summary>
public enum IniLineEndings
{
    /// <summary>No line endings at all — a single line, or an empty file.</summary>
    None = 0,

    /// <summary>Every line ends with <c>\n</c>.</summary>
    Lf,

    /// <summary>Every line ends with <c>\r\n</c>.</summary>
    CrLf,

    /// <summary>The file uses more than one convention.</summary>
    Mixed,
}
