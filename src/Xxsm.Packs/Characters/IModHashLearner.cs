using Xxsm.Core.Diagnostics;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Characters;

/// <summary>What a mod folder turned out to say about which character it is for.</summary>
public sealed record LearnedFromMod
{
    /// <summary>The mod folder that was read.</summary>
    public required string ModFolder { get; init; }

    /// <summary>The name to prefill a new character with, guessed from the folder name.</summary>
    public required string SuggestedName { get; init; }

    /// <summary>Every hash found, with its kind and the evidence for it.</summary>
    public required IReadOnlyList<ParsedHash> Hashes { get; init; }

    /// <summary>The files the hashes came from, relative to <see cref="ModFolder"/>.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>What the sorter makes of the mod, with every candidate and score behind it.</summary>
    public required SortDecision Decision { get; init; }

    /// <summary>The variant the hashes identify, or null when nothing claimed them.</summary>
    public string? MatchedVariantId => Decision.VariantId;

    /// <summary>Whether a character was identified, so the offer is to file it there, not create one.</summary>
    public bool WasMatched => Decision.VariantId is { Length: > 0 };

    /// <summary>Whether any hash was found at all.</summary>
    public bool HasHashes => Hashes.Count > 0;

    /// <summary>Anything odd noticed while reading the folder. Never fatal.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
}

/// <summary>Reads every hash a mod folder references, to teach a character or create one from the mod.</summary>
public interface IModHashLearner
{
    /// <summary>Reads a mod folder without changing it.</summary>
    /// <param name="modFolder">The mod folder to read.</param>
    /// <param name="data">The merged game data to match against.</param>
    /// <param name="request">The archive name and real folder name, when known, for the name fallback.</param>
    /// <param name="settings">Sorting thresholds. Defaults to <see cref="SortSettings.Default"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The folder does not exist or could not be listed; an
    /// unreadable file inside it is a diagnostic.</exception>
    Task<LearnedFromMod> LearnAsync(
        string modFolder,
        GameData data,
        SortRequest request = default,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default);
}
