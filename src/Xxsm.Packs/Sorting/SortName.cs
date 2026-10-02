using System.Text;

namespace Xxsm.Packs.Sorting;

/// <summary>Normalising and tokenising names for the name match: exact, or a whole token of four or more.</summary>
public static class SortName
{
    /// <summary>The shortest token that may identify a variant by containment.</summary>
    public const int MinimumTokenLength = 4;

    /// <summary>A whole chunk left by punctuation and whitespace. Considered first.</summary>
    public const int ChunkLevel = 0;

    /// <summary>A camel-case piece of a chunk. Considered only when no chunk matched.</summary>
    public const int PartLevel = 1;

    /// <summary>A name's comparison key: lowercase letters and digits, repeated characters collapsed to one.</summary>
    /// <param name="value">Any name: a folder, an archive, an INI section, a display name.</param>
    /// <returns>The key, which is empty when the input carried no letters or digits.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                continue;
            }

            var lowered = char.ToLowerInvariant(character);
            if (builder.Length > 0 && builder[^1] == lowered)
            {
                continue;
            }

            builder.Append(lowered);
        }

        return builder.ToString();
    }

    /// <summary>Splits a name at punctuation and then camel case, chunks before their pieces.</summary>
    /// <returns>The distinct non-empty tokens, in the order they appeared.</returns>
    public static IReadOnlyList<SortToken> Tokenize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var tokens = new List<SortToken>();
        var seen = new HashSet<(string Key, int Level)>();

        void Emit(string text, int level)
        {
            var key = Normalize(text);
            if (key.Length > 0 && seen.Add((key, level)))
            {
                tokens.Add(new SortToken(text, key, level));
            }
        }

        // Pass one: whole chunks. Pass two: the same chunks split on camel case.
        foreach (var chunk in Chunks(value))
        {
            Emit(chunk, ChunkLevel);
        }

        foreach (var chunk in Chunks(value))
        {
            foreach (var part in CamelParts(chunk))
            {
                Emit(part, PartLevel);
            }
        }

        return tokens;
    }

    /// <summary>Splits a name on everything that is not a letter or a digit.</summary>
    private static List<string> Chunks(string value)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

    /// <summary>Splits one chunk on camel-case and letter/digit boundaries.</summary>
    private static List<string> CamelParts(string chunk)
    {
        var parts = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < chunk.Length; i++)
        {
            if (current.Length > 0 && IsBoundary(chunk, i))
            {
                parts.Add(current.ToString());
                current.Clear();
            }

            current.Append(chunk[i]);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    /// <summary>Whether a token starts here: a case step up, a letter-digit switch, or an acronym's end.</summary>
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
}

/// <summary>One token of a name, as written and as compared.</summary>
/// <param name="Text">The token as it appeared, letters and digits only; its length is what the minimum
/// applies to.</param>
/// <param name="Key">The normalised form, which is what variants are indexed by.</param>
/// <param name="Level"><see cref="SortName.ChunkLevel"/> or <see cref="SortName.PartLevel"/>; a matcher must
/// try every chunk before any piece.</param>
public readonly record struct SortToken(string Text, string Key, int Level);
