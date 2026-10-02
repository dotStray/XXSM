using Xxsm.Core.Diagnostics;

namespace Xxsm.Core.Mods;

/// <summary>What a Mods folder contains, as read from disk. Knows nothing about packs.</summary>
public sealed record ModsInventory
{
    /// <summary>The folder that was scanned.</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>The character folders directly inside it, in name order, <c>Others/</c> included.</summary>
    public required IReadOnlyList<VariantFolder> VariantFolders { get; init; }

    /// <summary>Mods sitting loose at the root of the Mods folder rather than under a character.</summary>
    public required IReadOnlyList<InstalledMod> UnfiledMods { get; init; }

    /// <summary>Anything odd found on the way. Never fatal.</summary>
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>Every mod found, filed or not.</summary>
    public IEnumerable<InstalledMod> AllMods =>
        VariantFolders.SelectMany(folder => folder.Mods).Concat(UnfiledMods);

    /// <summary>How many mods were found in total.</summary>
    public int ModCount => VariantFolders.Sum(folder => folder.Mods.Count) + UnfiledMods.Count;

    /// <summary>How many of those are enabled.</summary>
    public int EnabledModCount => AllMods.Count(mod => mod.IsEnabled);
}

/// <summary>One character folder directly inside the Mods folder.</summary>
public sealed record VariantFolder
{
    /// <summary>The folder's name exactly as it is on disk, case preserved.</summary>
    public required string Name { get; init; }

    /// <summary>The folder's absolute path.</summary>
    public required string Path { get; init; }

    /// <summary>The mods inside it, in name order.</summary>
    public required IReadOnlyList<InstalledMod> Mods { get; init; }

    /// <summary>How many of them are enabled.</summary>
    public int EnabledCount => Mods.Count(mod => mod.IsEnabled);

    /// <summary>For a folder with no mods, how many other things sit in it; zero for a folder with mods.</summary>
    public int OtherEntryCount { get; init; }
}

/// <summary>One mod folder on disk.</summary>
public sealed record InstalledMod
{
    /// <summary>The mod folder's absolute path.</summary>
    public required string Path { get; init; }

    /// <summary>The folder's name exactly as it is on disk, including any <c>DISABLED_</c> prefix.</summary>
    public required string FolderName { get; init; }

    /// <summary>The folder's name with any disabled prefix removed. The mod's identity on disk.</summary>
    public required string Name { get; init; }

    /// <summary>Whether 3DMigoto will load this mod.</summary>
    public required bool IsEnabled { get; init; }

    /// <summary>The character folder it is filed under, or null when it is loose in the Mods folder.</summary>
    public required string? VariantFolderName { get; init; }

    /// <summary>When the folder's contents last changed; switching the mod on or off does not change it.</summary>
    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    /// <summary>The mod's <c>.xxsm/mod.json</c>, or <c>null</c> when it has none yet.</summary>
    public ModConfig? Config { get; init; }

    /// <summary>Why the mod's metadata could not be read, when it exists but is unreadable.</summary>
    public string? ConfigError { get; init; }

    /// <summary>What to call this mod in the UI: the user's name for it, else the folder's.</summary>
    public string DisplayName =>
        Config?.CustomName is { Length: > 0 } custom ? custom : Name;

    /// <summary>The variant a human filed this mod under, when they have.</summary>
    public string? VariantOverride => Config?.VariantOverride;

    /// <summary>When the user got this mod, as its metadata records; null when it has none.</summary>
    public DateTimeOffset? DateAdded => Config?.DateAdded;
}
