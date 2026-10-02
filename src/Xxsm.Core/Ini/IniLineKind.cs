namespace Xxsm.Core.Ini;

/// <summary>What one physical line of a 3DMigoto INI turned out to be.</summary>
public enum IniLineKind
{
    /// <summary>Nothing but spaces and tabs, or nothing at all.</summary>
    Blank = 0,

    /// <summary>A whole-line comment, starting <c>;</c> or <c>//</c>. There are no inline comments.</summary>
    Comment,

    /// <summary>A section header, <c>[LikeThis]</c>.</summary>
    SectionHeader,

    /// <summary>A <c>key = value</c> pair.</summary>
    Entry,

    /// <summary>A line inside a section with no <c>=</c>, such as <c>endif</c>: ordinary, not malformed.</summary>
    Directive,
}
