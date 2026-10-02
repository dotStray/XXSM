using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One tile in the character grid: a whole family in grouped mode, one variant in separate mode.</summary>
public sealed partial class CharacterTileViewModel : ObservableObject, IDisposable
{
    private readonly ITextCatalogue _text;
    private readonly GameData? _data;
    private readonly IPortraitCache? _portraits;
    private readonly CancellationTokenSource _loadCts = new();

    private IBitmapLease? _portraitHandle;
    private bool _disposed;

    /// <summary>Creates a tile.</summary>
    /// <param name="variant">The variant the tile stands for, or the family's base.</param>
    /// <param name="family">Every member the tile covers. One entry in separate mode.</param>
    /// <param name="modCount">How many mods are filed under it.</param>
    /// <param name="enabledModCount">How many of those are enabled.</param>
    /// <param name="text">The interface's wording, for the badge and the screen reader.</param>
    /// <param name="folderPath">The character's folder from the scan, or null when nothing is filed under it.</param>
    /// <param name="isPinned">Whether the user pinned this character to the top of the grid.</param>
    /// <param name="data">The merged game data, for the portrait; null leaves the initials.</param>
    /// <param name="portraits">The bounded portrait cache, or null for the same reason.</param>
    public CharacterTileViewModel(
        MergedVariant variant,
        IReadOnlyList<MergedVariant> family,
        int modCount,
        int enabledModCount,
        ITextCatalogue text,
        string? folderPath = null,
        bool isPinned = false,
        GameData? data = null,
        IPortraitCache? portraits = null)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(text);

        _text = text;
        _data = data;
        _portraits = portraits;

        Variant = variant;
        Family = family;
        FolderPath = folderPath;
        IsPinned = isPinned;

        InternalName = variant.InternalName;
        DisplayName = variant.DisplayName;
        ModCount = modCount;
        EnabledModCount = enabledModCount;

        SkinCount = Math.Max(0, family.Count - 1);

        IsCustom = variant.Origin is VariantOrigin.Custom or VariantOrigin.Modified;
        IsHidden = variant.Hidden;

        // No hashes is valid, not an error: badged rather than hidden.
        HashesPending = variant.HashesPendingFlag || variant.Hashes.Count == 0;

        Initial = DisplayName is { Length: > 0 }
            ? DisplayName[..1].ToUpperInvariant()
            : "?";
    }

    private CharacterTileViewModel(
        ITextCatalogue text, int modCount, int enabledModCount, bool isOthers, string? folderPath)
    {
        _text = text;
        Family = [];
        InternalName = string.Empty;
        IsOthers = isOthers;
        FolderPath = folderPath;
        DisplayName = text[isOthers ? nameof(Strings.Characters_Others) : nameof(Strings.Characters_Unsorted)];
        Initial = string.Empty;
        ModCount = modCount;
        EnabledModCount = enabledModCount;
    }

    /// <summary>The tile for the <c>Others</c> folder, where auto-sort files what nothing identified.</summary>
    /// <param name="modCount">How many mods are in it.</param>
    /// <param name="enabledModCount">How many of those are enabled.</param>
    /// <param name="folderPath">The folder, as scanned, so <em>Open folder</em> can open it.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="updateCount">How many of those have a newer version on GameBanana.</param>
    public static CharacterTileViewModel Others(
        int modCount, int enabledModCount, string folderPath, ITextCatalogue text, int updateCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(text);

        return new CharacterTileViewModel(text, modCount, enabledModCount, isOthers: true, folderPath)
        {
            UpdateCount = updateCount,
        };
    }

    /// <summary>The tile for mods no character owns: loose, in <c>Others</c>, or in an unclaimed folder.</summary>
    /// <param name="modCount">How many such mods there are.</param>
    /// <param name="enabledModCount">How many of those are enabled.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="updateCount">How many of those have a newer version on GameBanana.</param>
    /// <returns>A tile with no <see cref="Variant"/>, which opens the ordinary mod page.</returns>
    public static CharacterTileViewModel Unsorted(
        int modCount, int enabledModCount, ITextCatalogue text, int updateCount = 0)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new CharacterTileViewModel(text, modCount, enabledModCount, isOthers: false, folderPath: null)
        {
            UpdateCount = updateCount,
        };
    }

    /// <summary>The variant, or the family's base in grouped mode; null only for the two place tiles.</summary>
    public MergedVariant? Variant { get; }

    /// <summary>Whether this tile is a place, not a character: it cannot be edited, pinned or aimed at.</summary>
    public bool IsPlace => Variant is null;

    /// <summary>Whether this is the <see cref="Others"/> tile.</summary>
    public bool IsOthers { get; }

    /// <summary>Whether this is the <see cref="Unsorted"/> tile.</summary>
    public bool IsUnsorted => IsPlace && !IsOthers;

    /// <summary>Every member the tile covers. One entry in separate mode.</summary>
    public IReadOnlyList<MergedVariant> Family { get; }

    /// <summary>The variant's id.</summary>
    public string InternalName { get; }

    /// <summary>Where this character's mods are on disk, or null when there is no folder for it.</summary>
    public string? FolderPath { get; }

    /// <summary>Whether there is a folder to open.</summary>
    public bool HasFolder => FolderPath is { Length: > 0 };

    /// <summary>The name to show.</summary>
    public string DisplayName { get; }

    /// <summary>The stand-in for the portrait until it decodes, and for a variant with none.</summary>
    public string Initial { get; }

    /// <summary>The decoded portrait, or null until it loads, or permanently for a variant with none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    private Bitmap? _portrait;

    /// <summary>Whether <see cref="Portrait"/> has a bitmap to show.</summary>
    public bool HasPortrait => Portrait is not null;

    /// <summary>How many mods are filed under this tile.</summary>
    public int ModCount { get; }

    /// <summary>How many of those 3DMigoto will load.</summary>
    public int EnabledModCount { get; }

    /// <summary>How many skins the tile covers beyond the base character.</summary>
    public int SkinCount { get; }

    /// <summary>Whether the user created or edited this character.</summary>
    public bool IsCustom { get; }

    /// <summary>Whether the character is hidden; such a tile is drawn faded, with a way to show it again.</summary>
    public bool IsHidden { get; }

    /// <summary>Whether the pack has no hashes for it yet.</summary>
    public bool HashesPending { get; }

    /// <summary>Whether the user pinned this character to the top of the grid.</summary>
    public bool IsPinned { get; }

    /// <summary>How many mods here have a newer version on GameBanana; zero while GameBanana is off.</summary>
    public int UpdateCount { get; init; }

    /// <summary>Whether the tile carries the update mark.</summary>
    public bool HasUpdates => UpdateCount > 0;

    /// <summary>Whether any mod under this tile is enabled.</summary>
    public bool HasEnabledMods => EnabledModCount > 0;

    /// <summary>The characters and outfits here with more than one mod on, by name; empty on the place tiles.</summary>
    public IReadOnlyList<string> ClashingVariants { get; init; } = [];

    /// <summary>Whether two or more mods are on for one character or outfit: the count turns yellow.</summary>
    public bool HasClash => !IsPlace && ClashingVariants.Count > 0;

    /// <summary>Whether mods are on and none clash: the count turns green.</summary>
    public bool IsOn => HasEnabledMods && !HasClash;

    /// <summary>The count's hover text: mods, how many are on, and which have a clash; not colour alone.</summary>
    public string CountToolTip => HasClash
        ? _text.Format(nameof(Strings.Characters_Count_Clash), _text.Mods(ModCount), EnabledModCount, string.Join(", ", ClashingVariants))
        : HasEnabledMods
            ? _text.Format(nameof(Strings.Characters_Count_On), _text.Mods(ModCount), EnabledModCount)
            : _text.Format(nameof(Strings.Characters_Count_None), _text.Mods(ModCount));

    /// <summary>Whether there is anything to count. Hides the badge when there is not.</summary>
    public bool HasMods => ModCount > 0;

    /// <summary>Whether the tile covers more than one variant.</summary>
    public bool HasSkins => SkinCount > 0;

    /// <summary>The mod count as text.</summary>
    public string ModCountText => ModCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>The skins badge, shown only in grouped mode when there is a family.</summary>
    public string SkinCountText => SkinCount == 1
        ? _text[nameof(Strings.Characters_Badge_Skins_One)]
        : _text.Format(nameof(Strings.Characters_Badge_Skins_Many), SkinCount);

    /// <summary>What a screen reader says, with every state in words.</summary>
    public string AutomationName => HasClash
        ? _text.Format(nameof(Strings.Characters_Tile_Automation_Clash), BaseAutomationName, string.Join(", ", ClashingVariants))
        : BaseAutomationName;

    private string BaseAutomationName =>
        ModCount == 0
            ? _text.Format(nameof(Strings.Characters_Tile_Automation_Empty), DisplayName)
            : HasUpdates
                ? _text.Format(
                    nameof(Strings.Characters_Tile_Automation_Updates), DisplayName, ModCount, EnabledModCount)
                : _text.Format(
                    nameof(Strings.Characters_Tile_Automation), DisplayName, ModCount, EnabledModCount);

    /// <summary>Leases and decodes the portrait off the calling thread; safe to call and forget.</summary>
    public async Task LoadPortraitAsync()
    {
        if (_data is null || _portraits is null || Variant is not { } variant)
        {
            return;
        }

        IBitmapLease? handle;

        try
        {
            handle = await _portraits.AcquireAsync(_data, variant, _loadCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Discarded while decoding: release rather than assign, so a dead tile pins nothing.
        if (_disposed)
        {
            handle?.Dispose();
            return;
        }

        _portraitHandle = handle;
        Portrait = handle?.Bitmap;
    }

    /// <summary>Releases the portrait lease, if one was taken out.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadCts.Cancel();
        _loadCts.Dispose();
        _portraitHandle?.Dispose();
    }
}
