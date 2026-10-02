using Xxsm.Core.Diagnostics;

namespace Xxsm.Packs.Loading;

/// <summary>One problem found while loading a pack, in plain language and pointing at what it describes.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Code">A stable machine-readable code, for filtering and for tests.</param>
/// <param name="Message">Plain language, written to be shown to a non-programmer unedited.</param>
/// <param name="File">The pack file it was found in, when there is one.</param>
/// <param name="Subject">The variant or key it relates to, when there is one.</param>
public sealed record PackDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string? File = null,
    string? Subject = null)
{
    /// <summary>What the problem is about, in detail enough to fix it; null when the subject says it all.</summary>
    public PackDiagnosticTarget? Target { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        Subject is null
            ? $"[{Severity}] {Code}: {Message}"
            : $"[{Severity}] {Code}: {Message} ({Subject})";
}

/// <summary>What a <see cref="PackDiagnostic"/> is about, for a button that fixes it.</summary>
public sealed record PackDiagnosticTarget
{
    /// <summary>Every character involved, by internal name, the one to act on first.</summary>
    public IReadOnlyList<string> Characters { get; init; } = [];

    /// <summary>Which field is wrong: one of <see cref="PackDiagnosticFields"/>, or null.</summary>
    public string? Field { get; init; }

    /// <summary>The hash, or the text given as one, when the problem is about a single hash.</summary>
    public string? Hash { get; init; }

    /// <summary>The attribute, when the problem is about one.</summary>
    public string? Attribute { get; init; }

    /// <summary>The attribute value, when the problem is about one.</summary>
    public string? Value { get; init; }
}

/// <summary>The fields a <see cref="PackDiagnosticTarget.Field"/> names.</summary>
public static class PackDiagnosticFields
{
    /// <summary>A character's display name.</summary>
    public const string DisplayName = "displayName";

    /// <summary>A character's internal name.</summary>
    public const string InternalName = "internalName";

    /// <summary>A character's Mods folder name.</summary>
    public const string ModFilesName = "modFilesName";

    /// <summary>Which character a character is an outfit of.</summary>
    public const string BaseCharacterId = "baseCharacterId";

    /// <summary>Which outfit is its family's default.</summary>
    public const string DefaultOutfit = "isDefaultVariant";

    /// <summary>A character's portrait.</summary>
    public const string Image = "image";

    /// <summary>A character's hashes.</summary>
    public const string Hashes = "hashes";

    /// <summary>The pack's list of hashes to ignore.</summary>
    public const string IgnoredHashes = "ignoredHashes";

    /// <summary>A character's value for an attribute.</summary>
    public const string AttributeValue = "attributes";

    /// <summary>The game's attribute list.</summary>
    public const string AttributeDefinition = "game.attributes";

    /// <summary>The game's name.</summary>
    public const string GameName = "game.displayName";

    /// <summary>The game's id.</summary>
    public const string GameId = "game.gameId";

    /// <summary>The pack's version.</summary>
    public const string PackVersion = "manifest.packVersion";
}
