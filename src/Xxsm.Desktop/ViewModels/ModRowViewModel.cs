using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Core.Mods;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One row in the character detail view's mod grid.</summary>
public sealed partial class ModRowViewModel : ObservableObject
{
    private readonly ITextCatalogue _text;

    /// <summary>Creates a row.</summary>
    /// <param name="mod">The mod as the last scan found it.</param>
    /// <param name="text">The interface's wording, for the status text.</param>
    /// <param name="skinName">Which skin this mod is under, while all skins are shown at once; else null.</param>
    public ModRowViewModel(InstalledMod mod, ITextCatalogue text, string? skinName = null)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(text);

        Mod = mod;
        _text = text;
        SkinName = skinName;
    }

    /// <summary>The mod as the last scan found it.</summary>
    public InstalledMod Mod { get; }

    /// <summary>Whether this row's tick box is ticked in <em>Select</em> mode; mirrors the grid's selection.</summary>
    [ObservableProperty]
    private bool _isTicked;

    /// <summary>The mod's own absolute folder path. The grid's identity for a row.</summary>
    public string Path => Mod.Path;

    /// <summary>What identifies this mod across a rescan: its character folder and name without the prefix.</summary>
    public string SelectionKey => (Mod.VariantFolderName ?? string.Empty) + "/" + Mod.Name;

    /// <summary>The user's name for it, else the folder's.</summary>
    public string DisplayName => Mod.DisplayName;

    /// <summary>The folder's name on disk as a file manager shows it, <c>DISABLED_</c> prefix and all.</summary>
    public string FolderName => Mod.FolderName;

    /// <summary>The folder's name with any <c>DISABLED_</c> prefix removed. What a rename edits.</summary>
    public string BareFolderName => Mod.Name;

    /// <summary>Whether <see cref="FolderName"/> and <see cref="BareFolderName"/> differ.</summary>
    public bool IsFolderPrefixed => !string.Equals(FolderName, BareFolderName, StringComparison.Ordinal);

    /// <summary>Which skin this mod is filed under, while the grid is showing all of them, else null.</summary>
    public string? SkinName { get; }

    /// <summary>Whether <see cref="SkinName"/> has something to show.</summary>
    public bool HasSkinName => SkinName is { Length: > 0 };

    /// <summary>Whether the user filed this mod by hand, so auto-sort leaves it where it is.</summary>
    public bool IsFiledByYou => Mod.VariantOverride is { Length: > 0 };

    /// <summary>Whether 3DMigoto will load it.</summary>
    public bool IsEnabled => Mod.IsEnabled;

    /// <summary>"Enabled" or "Disabled", for the grid column — never colour alone.</summary>
    public string StatusText => IsEnabled
        ? _text[nameof(Strings.CharacterDetail_Status_Enabled)]
        : _text[nameof(Strings.CharacterDetail_Status_Disabled)];

    /// <summary>Who made it, when the mod's own metadata says.</summary>
    public string? Author => Mod.Config?.Author;

    /// <summary>The author's own version string.</summary>
    public string? Version => Mod.Config?.Version;

    /// <summary>Free text the user wrote about this mod.</summary>
    public string? Notes => Mod.Config?.Notes;

    /// <summary>Where the mod came from.</summary>
    public string? ModUrl => Mod.Config?.ModUrl;

    /// <summary>Whether there is an address to open.</summary>
    public bool HasModUrl => ModUrl is { Length: > 0 };

    /// <summary>What the mod is, as plain text, when its metadata says.</summary>
    public string? Description => Mod.Config?.Description;

    /// <summary>When XXSM first saw the mod.</summary>
    public DateTimeOffset? DateAdded => Mod.Config?.DateAdded;

    /// <summary>When the user got the mod, written out, or null when nothing records it.</summary>
    public string? DateAddedText => Format(DateAdded);

    /// <summary>When the mod folder's contents last changed, written out.</summary>
    public string? DateModifiedText => Format(Mod.LastWriteTimeUtc);

    private static string? Format(DateTimeOffset? value) =>
        value is { } moment ? Resources.TextDates.Date(moment) : null;

    /// <summary>Whether the mod is linked to a GameBanana page, so its updates are checked.</summary>
    public bool IsLinkedToGameBanana => Mod.Config?.IsLinkedToGameBanana == true;

    /// <summary>When its updates were last checked, as a date, or null when they never were.</summary>
    public string? LastCheckedText => Format(Mod.Config?.GameBanana?.LastChecked);

    /// <summary>The mod's GameBanana page, when it records one.</summary>
    public long? GameBananaModId => Mod.Config?.GameBanana?.ModId;

    /// <summary>Whether the last update check found a newer version on the mod's page.</summary>
    public bool HasUpdate => Mod.Config?.GameBanana?.UpdateAvailable == true;

    /// <summary>What the update mark means, on hover and to a screen reader; empty when nothing is waiting.</summary>
    public string UpdateTip => !HasUpdate
        ? string.Empty
        : UpdateVersions is { } versions
            ? _text.Format(nameof(Strings.ModRow_Update_Versions), versions)
            : _text[nameof(Strings.ModRow_Update)];

    /// <summary>The version change the last check found, written out, or null when it is not both known.</summary>
    public string? UpdateVersions =>
        HasUpdate
        && Version is { Length: > 0 } installed
        && Mod.Config?.GameBanana?.LatestVersion is { Length: > 0 } latest
            ? _text.Format(nameof(Strings.CharacterDetail_Detail_Update_Versions), installed, latest)
            : null;

    /// <summary>Whether there is a version change to show beside the update line.</summary>
    public bool HasUpdateVersions => UpdateVersions is { Length: > 0 };

    /// <summary>Whether <c>.xxsm/mod.json</c> could not be read. Shown rather than hidden.</summary>
    public bool HasConfigError => Mod.ConfigError is { Length: > 0 };

    /// <summary>The read failure, in the operating system's own words.</summary>
    public string? ConfigError => Mod.ConfigError;
}
