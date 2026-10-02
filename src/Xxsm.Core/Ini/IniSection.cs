namespace Xxsm.Core.Ini;

/// <summary>A run of lines under one section header, or the preamble before the first header.</summary>
public sealed class IniSection
{
    private readonly List<IniLine> _lines = [];
    private List<IniLine>? _entries;

    internal IniSection(int index, string name, IniLine? header)
    {
        Index = index;
        Name = name;
        Header = header;
    }

    /// <summary>Position of this section in <see cref="IniDocument.Sections"/>.</summary>
    public int Index { get; }

    /// <summary>The name as written, trimmed. Empty for the preamble.</summary>
    public string Name { get; }

    /// <summary>The header line, or null for the preamble section.</summary>
    public IniLine? Header { get; }

    /// <summary>Every line in the section, in order, including the header, blanks and comments.</summary>
    public IReadOnlyList<IniLine> Lines => _lines;

    /// <summary>The <see cref="IniLineKind.Entry"/> lines of this section, in order.</summary>
    public IReadOnlyList<IniLine> Entries =>
        _entries ??= _lines.FindAll(line => line.Kind == IniLineKind.Entry);

    /// <summary>Whether this is the implicit section holding lines before the first header.</summary>
    public bool IsPreamble => Header is null;

    internal void Add(IniLine line) => _lines.Add(line);

    /// <summary>Every entry with this key, ignoring case, in file order.</summary>
    /// <param name="key">The key to look for.</param>
    /// <returns>The matching entry lines, possibly none.</returns>
    public IReadOnlyList<IniLine> GetAll(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Entries.Where(line => string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The first entry with this key, ignoring case, as 3DMigoto uses it; null when there is none.</summary>
    /// <param name="key">The key to look for.</param>
    /// <returns>The entry line, or null.</returns>
    public IniLine? Find(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Entries.FirstOrDefault(line => string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The value of the first entry with this key, or null.</summary>
    /// <param name="key">The key to look for.</param>
    /// <returns>The value as written, trimmed, or null if the section has no such key.</returns>
    public string? GetValue(string key) => Find(key)?.Value;

    /// <summary>Whether the section's name starts with <paramref name="prefix"/>, ignoring case.</summary>
    /// <param name="prefix">The prefix to test, for example <c>TextureOverride</c>.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public bool NameStartsWith(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override string ToString() => IsPreamble ? "(preamble)" : $"[{Name}]";
}
