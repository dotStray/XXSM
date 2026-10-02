using System.Text;
using Xxsm.Core.Hashes;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Hashes;

/// <summary>What shape the pasted text turned out to be.</summary>
public enum HashPasteShape
{
    /// <summary>There was nothing to read.</summary>
    Empty = 0,

    /// <summary>An upstream <c>hash.json</c>.</summary>
    HashJson,

    /// <summary>3DMigoto INI text, with at least one section header.</summary>
    Ini,

    /// <summary>Bare hashes, one per line, with no section headers.</summary>
    BareHashes,
}

/// <summary>One hash the parser accepted, and what told it the kind.</summary>
/// <param name="Entry">The hash, ready for <c>ICharacterEditor.AddHashesAsync</c>.</param>
/// <param name="Evidence">Where it came from and how its kind was decided, for the confirm step.</param>
/// <param name="KindWasInferred">Whether the kind was guessed from a section name rather than stated.</param>
public sealed record ParsedHash(PackHashEntry Entry, string Evidence, bool KindWasInferred);

/// <summary>Something in the paste that looked like a hash but was not one.</summary>
/// <param name="Text">The offending text, trimmed.</param>
/// <param name="Reason">Why it was not kept, in words a user can act on.</param>
/// <param name="Line">The 1-based line it was on, or null for JSON input.</param>
public sealed record HashPasteRejection(string Text, string Reason, int? Line);

/// <summary>What <see cref="HashPaste.Parse"/> made of the text.</summary>
public sealed record HashPasteResult
{
    /// <summary>The hashes that were understood, deduplicated, in the order they were read.</summary>
    public required IReadOnlyList<ParsedHash> Hashes { get; init; }

    /// <summary>Things that looked like hashes and were not, each with a reason.</summary>
    public required IReadOnlyList<HashPasteRejection> Rejected { get; init; }

    /// <summary>What the text turned out to be.</summary>
    public required HashPasteShape Shape { get; init; }

    /// <summary>How many lines were read past: blanks, comments and the ordinary INI settings around a hash.</summary>
    public required int IgnoredLines { get; init; }

    /// <summary>Whether anything was understood.</summary>
    public bool IsEmpty => Hashes.Count == 0;

    /// <summary>The hashes as entries, for handing straight to the editor.</summary>
    public IReadOnlyList<PackHashEntry> Entries => [.. Hashes.Select(hash => hash.Entry)];

    /// <summary>How many of each kind were found, commonest first.</summary>
    public IReadOnlyList<(HashKind Kind, int Count)> CountByKind() =>
    [
        .. Hashes
            .GroupBy(hash => hash.Entry.Kind)
            .Select(group => (Kind: group.Key, Count: group.Count()))
            .OrderByDescending(pair => pair.Count)
            .ThenBy(pair => pair.Kind),
    ];
}

/// <summary>Reads hashes out of a paste: bare hashes, <c>hash =</c> lines, INI blocks, a hash.json.</summary>
/// <remarks>A well-formed hash is always kept; its kind is <see cref="HashKind.Unknown"/> when nothing decides
/// it.</remarks>
public static class HashPaste
{
    /// <summary>The name a pasted INI is reported under, since there is no file.</summary>
    private const string PastedInName = "pasted text";

    /// <summary>Section-name tokens that name a 3DMigoto buffer or texture; only the trailing one counts.</summary>
    private static readonly Dictionary<string, HashKind> SectionMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ib"] = HashKind.Ib,
        ["index"] = HashKind.Ib,
        ["indexbuffer"] = HashKind.Ib,
        ["position"] = HashKind.PositionVb,
        ["pos"] = HashKind.PositionVb,
        ["blend"] = HashKind.BlendVb,
        ["texcoord"] = HashKind.TexcoordVb,
        ["draw"] = HashKind.DrawVb,
        ["vb"] = HashKind.DrawVb,
        ["diffuse"] = HashKind.Texture,
        ["lightmap"] = HashKind.Texture,
        ["normalmap"] = HashKind.Texture,
        ["shadowramp"] = HashKind.Texture,
        ["metalmap"] = HashKind.Texture,
        ["materialmap"] = HashKind.Texture,
        ["texture"] = HashKind.Texture,
    };

    /// <summary>Reads every hash the text contains. Never throws: an unreadable paste gives reasons instead.</summary>
    /// <param name="text">Whatever the user pasted, or the contents of one file.</param>
    /// <param name="source">A name for where the text came from, for the evidence lines; null for a paste.</param>
    public static HashPasteResult Parse(string? text, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashPasteResult
            {
                Hashes = [],
                Rejected = [],
                Shape = HashPasteShape.Empty,
                IgnoredLines = 0,
            };
        }

        var name = string.IsNullOrWhiteSpace(source) ? PastedInName : source;

        // A hash.json starts with '[' as an INI section does: try JSON first and fall through.
        if (TryParseHashJson(text, name) is { } json)
        {
            return json;
        }

        return ParseLines(text, name);
    }

    /// <summary>Reads every hash out of an upstream <c>hash.json</c>'s bytes.</summary>
    /// <param name="bytes">The file's contents.</param>
    /// <param name="source">A name for the file, used in the evidence lines.</param>
    public static HashPasteResult ParseHashJson(ReadOnlySpan<byte> bytes, string? source = null) =>
        FromComponents(
            UpstreamHashReader.Read(bytes, source),
            string.IsNullOrWhiteSpace(source) ? PastedInName : source,
            keepEveryPlacement: false);

    /// <summary>Reads every entry of an upstream <c>hash.json</c> as a pack records them, none merged.</summary>
    /// <param name="bytes">The file's contents.</param>
    /// <param name="source">A name for the file, used in the evidence lines.</param>
    public static HashPasteResult ReadHashJsonEntries(ReadOnlySpan<byte> bytes, string? source = null) =>
        FromComponents(
            UpstreamHashReader.Read(bytes, source),
            string.IsNullOrWhiteSpace(source) ? PastedInName : source,
            keepEveryPlacement: true);

    private static HashPasteResult? TryParseHashJson(string text, string source)
    {
        var trimmed = text.TrimStart();

        if (trimmed.Length == 0 || (trimmed[0] != '[' && trimmed[0] != '{'))
        {
            return null;
        }

        var read = UpstreamHashReader.Read(Encoding.UTF8.GetBytes(text), source);

        return read.Components.Count == 0 ? null : FromComponents(read, source, keepEveryPlacement: false);
    }

    private static HashPasteResult FromComponents(UpstreamHashResult read, string source, bool keepEveryPlacement)
    {
        var hashes = new List<ParsedHash>();
        var rejected = new List<HashPasteRejection>();

        void Keep(PackHashEntry entry, string evidence)
        {
            if (!keepEveryPlacement)
            {
                Add(hashes, entry, evidence, inferred: false);
                return;
            }

            if (!hashes.Exists(existing => SamePlacement(existing.Entry, entry)))
            {
                hashes.Add(new ParsedHash(entry, evidence, KindWasInferred: false));
            }
        }

        foreach (var diagnostic in read.Diagnostics)
        {
            rejected.Add(new HashPasteRejection(source, diagnostic.Message, Line: null));
        }

        foreach (var component in read.Components)
        {
            var where = string.IsNullOrWhiteSpace(component.ComponentName)
                ? source
                : $"{PathDisplay.Show(source)}, component '{component.ComponentName}'";

            foreach (var (field, kind, raw) in Fields(component))
            {
                if (HashText.Normalize(raw) is not { } hash)
                {
                    // An empty string upstream means absent, not an empty hash.
                    continue;
                }

                Keep(new PackHashEntry
                {
                    Kind = kind,
                    Hash = hash,
                    Component = component.ComponentName,
                }, $"{where}: the {field} field");
            }

            foreach (var texture in component.Textures())
            {
                Keep(new PackHashEntry
                {
                    Kind = HashKind.Texture,
                    Hash = texture.Hash,
                    Component = component.ComponentName,
                    TextureKind = texture.Kind,
                    Slot = texture.Slot,
                }, $"{where}: a {texture.Kind} texture");
            }
        }

        return new HashPasteResult
        {
            Hashes = hashes,
            Rejected = rejected,
            Shape = HashPasteShape.HashJson,
            IgnoredLines = 0,
        };
    }

    /// <summary>Whether two entries are one hash in one place: component, kind, texture kind and slot.</summary>
    private static bool SamePlacement(PackHashEntry one, PackHashEntry two) =>
        one.Kind == two.Kind
        && one.Slot == two.Slot
        && string.Equals(one.Hash, two.Hash, StringComparison.OrdinalIgnoreCase)
        && string.Equals(one.Component ?? string.Empty, two.Component ?? string.Empty, StringComparison.Ordinal)
        && string.Equals(one.TextureKind, two.TextureKind, StringComparison.Ordinal);

    private static IEnumerable<(string Field, HashKind Kind, string? Raw)> Fields(UpstreamHashComponent component)
    {
        yield return ("ib", HashKind.Ib, component.Ib);
        yield return ("position_vb", HashKind.PositionVb, component.PositionVb);
        yield return ("blend_vb", HashKind.BlendVb, component.BlendVb);
        yield return ("texcoord_vb", HashKind.TexcoordVb, component.TexcoordVb);
        yield return ("draw_vb", HashKind.DrawVb, component.DrawVb);
        yield return ("root_vs", HashKind.RootVs, component.RootVs);
    }

    private static HashPasteResult ParseLines(string text, string source)
    {
        var document = IniParser.ParseText(text, source);

        var hashes = new List<ParsedHash>();
        var rejected = new List<HashPasteRejection>();
        var ignored = 0;
        var sawSection = false;

        foreach (var line in document.Lines)
        {
            switch (line.Kind)
            {
                case IniLineKind.SectionHeader:
                    sawSection = true;
                    ignored++;
                    continue;

                case IniLineKind.Blank:
                case IniLineKind.Comment:
                    ignored++;
                    continue;

                case IniLineKind.Entry when IsHashKey(line.Key):
                    ReadHashEntry(line, document, source, hashes, rejected);
                    continue;

                case IniLineKind.Directive when HashText.Normalize(line.Text.Trim()) is { } bare:
                    Decide(SectionNameOf(document, line), bare, out var kind, out var marker);
                    Add(
                        hashes,
                        new PackHashEntry { Kind = kind, Hash = bare },
                        Describe(source, line.Number, SectionNameOf(document, line), marker),
                        inferred: marker is not null);
                    continue;

                case IniLineKind.Directive when HashList(line.Text) is { } several:
                    foreach (var one in several)
                    {
                        Decide(SectionNameOf(document, line), one, out var listKind, out var listMarker);
                        Add(
                            hashes,
                            new PackHashEntry { Kind = listKind, Hash = one },
                            Describe(source, line.Number, SectionNameOf(document, line), listMarker),
                            inferred: listMarker is not null);
                    }

                    continue;

                case IniLineKind.Directive when LooksLikeAFailedHash(line.Text.Trim()):
                    rejected.Add(new HashPasteRejection(
                        line.Text.Trim(),
                        $"A hash is 8 or 16 hexadecimal digits; this is {line.Text.Trim().Length}.",
                        line.Number));
                    continue;

                default:
                    ignored++;
                    continue;
            }
        }

        return new HashPasteResult
        {
            Hashes = hashes,
            Rejected = rejected,
            Shape = sawSection ? HashPasteShape.Ini : HashPasteShape.BareHashes,
            IgnoredLines = ignored,
        };
    }

    private static readonly char[] ListSeparators = [',', ';', ' ', '\t'];

    /// <summary>The hashes on a line that holds two or more and nothing else, or null.</summary>
    private static List<string>? HashList(string text)
    {
        var pieces = text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries);

        if (pieces.Length < 2)
        {
            return null;
        }

        var hashes = new List<string>(pieces.Length);

        foreach (var piece in pieces)
        {
            if (HashText.Normalize(piece) is not { } hash)
            {
                return null;
            }

            hashes.Add(hash);
        }

        return hashes;
    }

    private static void ReadHashEntry(
        IniLine line,
        IniDocument document,
        string source,
        List<ParsedHash> hashes,
        List<HashPasteRejection> rejected)
    {
        var section = SectionNameOf(document, line);

        // Extract rather than Normalize: the game reads "hash = 1a2b3c4d // note" as a hash.
        if (HashText.Extract(line.Value) is not { } hash)
        {
            rejected.Add(new HashPasteRejection(
                line.Text.Trim(),
                string.IsNullOrWhiteSpace(line.Value)
                    ? "This hash setting has no value."
                    : $"'{line.Value}' is not 8 or 16 hexadecimal digits.",
                line.Number));

            return;
        }

        Decide(section, hash, out var kind, out var marker);

        Add(
            hashes,
            new PackHashEntry { Kind = kind, Hash = hash },
            Describe(source, line.Number, section, marker),
            inferred: marker is not null);
    }

    private static bool IsHashKey(string? key) => string.Equals(key, "hash", StringComparison.OrdinalIgnoreCase);

    private static string? SectionNameOf(IniDocument document, IniLine line) =>
        line.SectionIndex >= 0 && line.SectionIndex < document.Sections.Length
            ? document.Sections[line.SectionIndex].Name
            : null;

    /// <summary>Decides a hash's kind from its length (16 digits is a shader hash) and else its section name.</summary>
    private static void Decide(string? sectionName, string hash, out HashKind kind, out string? marker)
    {
        if (hash.Length == HashText.ShaderLength)
        {
            kind = HashKind.RootVs;
            marker = null;
            return;
        }

        marker = TrailingMarker(sectionName);
        kind = marker is not null ? SectionMarkers[marker] : HashKind.Unknown;
    }

    /// <summary>The last real word of a section name, when that word names a buffer or texture.</summary>
    internal static string? TrailingMarker(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return null;
        }

        var tokens = Tokenise(sectionName);

        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];

            if (i > 0 && SectionMarkers.ContainsKey(tokens[i - 1] + token))
            {
                return tokens[i - 1] + token;
            }

            if (SectionMarkers.ContainsKey(token))
            {
                return token;
            }

            if (token.All(char.IsAsciiDigit) || token.Length <= 2)
            {
                continue;
            }

            return null;
        }

        return null;
    }

    /// <summary>Splits a section name into words at separators, camel case and letter-digit changes.</summary>
    private static List<string> Tokenise(string sectionName)
    {
        var stripped = StripSectionType(sectionName);
        var tokens = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < stripped.Length; i++)
        {
            var character = stripped[i];

            if (!char.IsAsciiLetterOrDigit(character))
            {
                Flush(tokens, current);
                continue;
            }

            if (current.Length > 0 && IsBoundary(stripped, i))
            {
                Flush(tokens, current);
            }

            current.Append(character);
        }

        Flush(tokens, current);
        return tokens;
    }

    private static bool IsBoundary(string value, int i)
    {
        var previous = value[i - 1];
        var current = value[i];

        if (char.IsAsciiDigit(current) != char.IsAsciiDigit(previous))
        {
            return true;
        }

        if (char.IsAsciiLetterUpper(current) && char.IsAsciiLetterLower(previous))
        {
            return true;
        }

        return char.IsAsciiLetterUpper(current)
            && char.IsAsciiLetterUpper(previous)
            && i + 1 < value.Length
            && char.IsAsciiLetterLower(value[i + 1]);
    }

    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    private static string StripSectionType(string sectionName)
    {
        var trimmed = sectionName.Trim();

        foreach (var prefix in (string[])["TextureOverride", "ShaderOverride", "ShaderRegex", "Resource"])
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..];
            }
        }

        return trimmed;
    }

    /// <summary>Whether a line that gave no hash was probably meant to be one, and worth reporting.</summary>
    private static bool LooksLikeAFailedHash(string text) =>
        text.Length is > 0 and <= 20 && text.All(Uri.IsHexDigit);

    private static string Describe(string source, int line, string? section, string? marker)
    {
        var where = section is { Length: > 0 }
            ? $"{PathDisplay.Show(source)} line {line.ToString(System.Globalization.CultureInfo.InvariantCulture)}, under [{section}]"
            : $"{PathDisplay.Show(source)} line {line.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        return marker is null
            ? where
            : $"{where} — the section name ends in '{marker}'";
    }

    private static void Add(
        List<ParsedHash> hashes, PackHashEntry entry, string evidence, bool inferred)
    {
        if (hashes.Any(existing =>
                string.Equals(existing.Entry.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase)
                && existing.Entry.Kind == entry.Kind))
        {
            return;
        }

        hashes.Add(new ParsedHash(entry, evidence, inferred));
    }
}
