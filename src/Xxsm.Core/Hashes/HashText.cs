namespace Xxsm.Core.Hashes;

/// <summary>Decides what a 3DMigoto hash looks like and how it is written down.</summary>
public static class HashText
{
    /// <summary>Length of a buffer or texture hash, in hex digits.</summary>
    public const int BufferLength = 8;

    /// <summary>Length of a shader hash, in hex digits.</summary>
    public const int ShaderLength = 16;

    /// <summary>Whether this is a hash XXSM will index: exactly 8 or 16 hex digits, nothing else.</summary>
    /// <returns><see langword="true"/> when it is a well-formed hash.</returns>
    public static bool IsHash(string? value) =>
        value is not null
        && value.Length is BufferLength or ShaderLength
        && value.All(Uri.IsHexDigit);

    /// <summary>Lowercases a hash, or returns null for anything else, an empty string included.</summary>
    /// <param name="value">The candidate text.</param>
    /// <returns>The lowercase hash, or null.</returns>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return IsHash(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    /// <summary>Pulls a hash from the start of an INI value, ignoring what follows it, as 3DMigoto does.</summary>
    /// <param name="value">The value as written in the INI, already trimmed.</param>
    /// <returns>The lowercase hash, or null when the value does not start with one.</returns>
    public static string? Extract(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().TrimStart();
        var length = 0;
        while (length < span.Length && Uri.IsHexDigit(span[length]))
        {
            length++;
        }

        if (length is not (BufferLength or ShaderLength))
        {
            return null;
        }

        if (length < span.Length && !char.IsWhiteSpace(span[length]))
        {
            return null;
        }

        return span[..length].ToString().ToLowerInvariant();
    }
}
