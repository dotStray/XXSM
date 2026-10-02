using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One installed copy of a mod about to be downloaded again, on the install panel.</summary>
/// <param name="copy">The copy, as the Mods folder scan found it.</param>
/// <param name="whereText">Where it is filed, in words: "under Xingqiu".</param>
public sealed class InstalledCopyViewModel(InstalledCopy copy, string whereText)
{
    /// <summary>The mod's own folder, for going to it.</summary>
    public string ModFolder { get; } = copy.ModFolder;

    /// <summary>The folder's name on disk.</summary>
    public string FolderName { get; } = copy.FolderName;

    /// <summary>What the mod is called.</summary>
    public string DisplayName { get; } = copy.DisplayName;

    /// <summary>Where it is filed, in words.</summary>
    public string WhereText { get; } = whereText;
}
