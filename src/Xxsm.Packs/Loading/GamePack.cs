using Xxsm.Packs.Model;

namespace Xxsm.Packs.Loading;

/// <summary>A loaded Game Pack as it sits on disk, before any overlay. Never mutated.</summary>
public sealed class GamePack
{
    /// <summary>Creates a loaded pack.</summary>
    /// <param name="directory">Where the pack is installed.</param>
    /// <param name="manifest">The parsed manifest.</param>
    /// <param name="game">The parsed game definition.</param>
    /// <param name="variants">Characters and objects together, in file order.</param>
    /// <param name="hashes">The hash index file, or null when the pack ships none.</param>
    /// <param name="diagnostics">Everything noticed while loading.</param>
    public GamePack(
        string directory,
        PackManifest manifest,
        GameDefinition game,
        IReadOnlyList<PackVariant> variants,
        HashIndexFile? hashes,
        IReadOnlyList<PackDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Directory = directory;
        Manifest = manifest;
        Game = game;
        Variants = variants;
        Hashes = hashes;
        Diagnostics = diagnostics;
    }

    /// <summary>Where the pack is installed on disk.</summary>
    public string Directory { get; }

    /// <summary>The manifest.</summary>
    public PackManifest Manifest { get; }

    /// <summary>The game definition, including the attribute schema.</summary>
    public GameDefinition Game { get; }

    /// <summary>Every variant from <c>variants.json</c>, in file order.</summary>
    public IReadOnlyList<PackVariant> Variants { get; }

    /// <summary>The hash index, or null. A pack with no hashes at all is valid.</summary>
    public HashIndexFile? Hashes { get; }

    /// <summary>Everything the loader noticed, worst first.</summary>
    public IReadOnlyList<PackDiagnostic> Diagnostics { get; }

    /// <summary>The game this pack describes.</summary>
    public string GameId => Manifest.GameId;

    /// <summary>The pack's version.</summary>
    public string PackVersion => Manifest.PackVersion;
}
