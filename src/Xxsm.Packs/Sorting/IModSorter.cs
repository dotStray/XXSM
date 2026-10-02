using Xxsm.Core.Mods;

namespace Xxsm.Packs.Sorting;

/// <summary>Everything about a mod not in its files: what the user already decided, and where it came from.</summary>
/// <param name="VariantOverride">The <c>variantOverride</c> from <c>.xxsm/mod.json</c>: a person's filing,
/// which the sorter never second-guesses.</param>
/// <param name="FolderName">The mod's real folder name, when the scanned root is a temporary one.</param>
/// <param name="ArchiveName">The archive it was installed from, without extension, when known.</param>
public readonly record struct SortRequest(
    string? VariantOverride = null,
    string? FolderName = null,
    string? ArchiveName = null);

/// <summary>Decides which variant a mod folder belongs to. Moves nothing.</summary>
public interface IModSorter
{
    /// <summary>Decides where one mod belongs.</summary>
    /// <param name="index">The index to score against, built once per pack load.</param>
    /// <param name="signals">What the mod folder says about itself.</param>
    /// <param name="request">What the user and the installer already know.</param>
    SortDecision Sort(SortIndex index, ModSignals signals, SortRequest request = default);
}
