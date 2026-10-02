using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Model;
using Xxsm.Packs.Sorting;

namespace Xxsm.Desktop.ViewModels;

/// <summary>What a cell of the Studio table does with what was typed: one draft edit, one Undo step.</summary>
public interface IStudioTableEdits
{
    /// <summary>Renames a character.</summary>
    void EditName(StudioCharacterRowViewModel row, string text);

    /// <summary>Gives a character a different internal name.</summary>
    void EditInternalName(StudioCharacterRowViewModel row, string text);

    /// <summary>Replaces a character's aliases with a comma-separated list.</summary>
    void EditAliases(StudioCharacterRowViewModel row, string text);

    /// <summary>Makes a character an outfit of the one named, or a character in its own right when blank.</summary>
    /// <param name="row">The row edited.</param>
    /// <param name="text">What was typed or chosen.</param>
    void EditOutfitOf(StudioCharacterRowViewModel row, string text);

    /// <summary>Sets one of the game's attributes on a character, or clears it when blank.</summary>
    /// <param name="row">The row edited.</param>
    /// <param name="attributeId">The attribute.</param>
    /// <param name="text">What was typed or chosen.</param>
    void EditAttribute(StudioCharacterRowViewModel row, string attributeId, string text);

    /// <summary>Makes a character's hashes exactly the ones in the text.</summary>
    /// <param name="row">The row edited.</param>
    /// <param name="text">What was left in the cell.</param>
    void EditHashes(StudioCharacterRowViewModel row, string text);
}

/// <summary>One character or object in the Studio table, rebuilt from the draft after every change.</summary>
/// <param name="variant">The character.</param>
/// <param name="hashes">The hashes that name it, in the draft's order.</param>
/// <param name="outfitOf">The name of the character it is an outfit of, as the chooser shows it, or
/// empty.</param>
/// <param name="errors">How many export-blocking problems are about it.</param>
/// <param name="warnings">How many other problems are about it.</param>
/// <param name="picture">Its picture, or null when it has none the table can show.</param>
/// <param name="ignoredHashes">Every hash the pack ignores, to count how many of this one's are among
/// them.</param>
/// <param name="hashCountText">What the Hashes cell says, when it is not just the count.</param>
public sealed partial class StudioCharacterRowViewModel(
    PackVariant variant,
    IReadOnlyList<string> hashes,
    string outfitOf,
    int errors,
    int warnings,
    StudioPictureViewModel? picture = null,
    IReadOnlySet<string>? ignoredHashes = null,
    string? hashCountText = null) : ObservableObject
{
    /// <summary>Whether the row's tick box shows ticked in <em>Select</em>: a read-out of the selection.</summary>
    [ObservableProperty]
    private bool _isTicked;

    /// <summary>The character as the draft holds it.</summary>
    public PackVariant Variant { get; } = variant;

    /// <summary>Its id, which the page keys selection on.</summary>
    public string InternalName => Variant.InternalName;

    /// <summary>Its name.</summary>
    public string DisplayName => Variant.DisplayName;

    /// <summary>The id of the character it is an outfit of, or empty.</summary>
    public string BaseCharacterId => Variant.BaseCharacterId ?? string.Empty;

    /// <summary>The character it is an outfit of, as the chooser names it, or empty.</summary>
    public string OutfitOf { get; } = outfitOf;

    /// <summary>Whether it is an outfit of another character.</summary>
    public bool IsOutfit => Variant.BaseCharacterId is not null;

    /// <summary>Its aliases, comma-separated.</summary>
    public string AliasesText => string.Join(", ", Variant.Aliases ?? []);

    /// <summary>One attribute's value as its cell shows it, or empty.</summary>
    /// <returns><c>4</c>, <c>fire</c>, or <c>sword, bow</c>.</returns>
    public string AttributeText(string attributeId)
    {
        var attributes = Variant.Attributes;

        if (attributes is null)
        {
            return string.Empty;
        }

        var value = attributes.FirstOrDefault(pair => string.Equals(pair.Key, attributeId, StringComparison.OrdinalIgnoreCase)).Value;

        return value switch
        {
            null => string.Empty,
            { Number: { } number } => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    /// <summary>The hashes that name it.</summary>
    public IReadOnlyList<string> Hashes { get; } = hashes;

    /// <summary>Its hashes, comma-separated, as the Hashes cell edits them.</summary>
    public string HashesText => string.Join(", ", Hashes);

    /// <summary>How many hash entries name it, the ignored ones included. Zero is normal.</summary>
    public int HashCount => Hashes.Count;

    /// <summary>How many of them the pack ignores, so the sorter never scores them. Usually none.</summary>
    public int IgnoredHashCount { get; } =
        ignoredHashes is null ? 0 : hashes.Count(hash => ignoredHashes.Contains(hash));

    /// <summary>Whether any of its hashes are ignored, so the cell has to say so.</summary>
    public bool HasIgnoredHashes => IgnoredHashCount > 0;

    /// <summary>What the Hashes cell shows: <c>17</c>, or <c>17 (3 ignored)</c>; the total includes them.</summary>
    public string HashCountText { get; } =
        hashCountText ?? hashes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether it has a portrait.</summary>
    public bool HasPortrait => !string.IsNullOrWhiteSpace(Variant.Image);

    /// <summary>Its picture as the table shows it, or null when there is none it can show.</summary>
    public StudioPictureViewModel? Picture { get; } = picture;

    /// <summary>What stands in for a missing picture: the first letter of its name.</summary>
    public string Initials => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "?";

    /// <summary>How many export-blocking problems are about it.</summary>
    public int ErrorCount { get; } = errors;

    /// <summary>How many other problems are about it.</summary>
    public int WarningCount { get; } = warnings;

    /// <summary>Whether anything would stop the pack being exported because of it.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Whether there is a warning about it and no error.</summary>
    public bool HasOnlyWarnings => ErrorCount == 0 && WarningCount > 0;

    /// <summary>Whether anything at all is wrong with it, so its Problems cell can be pressed.</summary>
    public bool HasProblems => ErrorCount + WarningCount > 0;

    /// <summary>Whether another row would draw exactly as this one does.</summary>
    /// <returns>True when nothing on screen would differ, so the table can keep the row it has.</returns>
    public bool LooksLike(StudioCharacterRowViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return ReferenceEquals(Variant, other.Variant)
               && ReferenceEquals(Picture, other.Picture)
               && string.Equals(OutfitOf, other.OutfitOf, StringComparison.Ordinal)
               && ErrorCount == other.ErrorCount
               && WarningCount == other.WarningCount
               && string.Equals(HashCountText, other.HashCountText, StringComparison.Ordinal)
               && Hashes.SequenceEqual(other.Hashes, StringComparer.Ordinal);
    }
}

/// <summary>One character's picture in the Studio table, kept while it is the same file; owned by the page.</summary>
public sealed partial class StudioPictureViewModel(string key) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private IBitmapLease? _lease;
    private bool _disposed;

    /// <summary>The path and version it was made for.</summary>
    public string Key { get; } = key;

    /// <summary>The decoded picture, or null until it loads, or for good when it cannot be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBitmap))]
    private Bitmap? _bitmap;

    /// <summary>Whether there is a picture to draw; the initials show otherwise.</summary>
    public bool HasBitmap => Bitmap is not null;

    /// <summary>Reads and decodes the picture. Safe to start and not wait for.</summary>
    /// <param name="acquire">Leases the bitmap from the table's cache.</param>
    public async Task LoadAsync(Func<CancellationToken, Task<IBitmapLease?>> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);

        IBitmapLease? lease;

        try
        {
            lease = await acquire(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Replaced or closed while decoding: let the lease go at once.
        if (_disposed)
        {
            lease?.Dispose();
            return;
        }

        _lease = lease;
        Bitmap = lease?.Bitmap;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        Bitmap = null;
        _lease?.Dispose();
    }
}

/// <summary>A character the Outfit of cell can name.</summary>
/// <param name="InternalName">Its id.</param>
/// <param name="Label">Its name, with its id when another character shares the name.</param>
public sealed record StudioOutfitChoice(string InternalName, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One of the game's attributes, as a column of the Studio table.</summary>
/// <param name="Id">The attribute's id.</param>
/// <param name="Header">What the column is called.</param>
/// <param name="Suggestions">The values the game declares, offered while typing; empty for a number.</param>
public sealed record StudioAttributeColumn(string Id, string Header, IReadOnlyList<string> Suggestions)
{
    /// <summary>Whether two column lists would draw the same columns.</summary>
    /// <returns>True when they have the same columns in the same order.</returns>
    public static bool SameColumns(IReadOnlyList<StudioAttributeColumn> left, IReadOnlyList<StudioAttributeColumn> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.Count == right.Count
               && left.Zip(right).All(pair =>
                   string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal)
                   && string.Equals(pair.First.Header, pair.Second.Header, StringComparison.Ordinal)
                   && pair.First.Suggestions.SequenceEqual(pair.Second.Suggestions, StringComparer.Ordinal));
    }
}

/// <summary>What a problem's button does.</summary>
public enum StudioProblemAction
{
    /// <summary>No button: nothing in Studio can fix it.</summary>
    None,

    /// <summary>Selects the character it is about.</summary>
    Select,

    /// <summary>Opens the list of hashes two or more families share.</summary>
    SharedHashes,

    /// <summary>Opens the list of characters that carry one hash.</summary>
    HashCarriers,

    /// <summary>Selects the character and asks for a picture.</summary>
    Picture,

    /// <summary>Copies a picture that is a file on this computer into the draft.</summary>
    CopyPictureIn,

    /// <summary>Opens the character in the Character Manager with the field to fix focused.</summary>
    EditField,

    /// <summary>Selects the characters and starts editing the table cell to fix.</summary>
    EditCell,

    /// <summary>Asks which outfit is its family's default.</summary>
    ChooseDefault,

    /// <summary>Opens the character's hashes, with the one at fault marked.</summary>
    Hashes,

    /// <summary>Offers to claim or delete hashes filed under a name no character has.</summary>
    UnclaimedHashes,

    /// <summary>Takes an entry off the pack's list of hashes to ignore.</summary>
    Unignore,

    /// <summary>Opens Game settings at the attribute or field to fix.</summary>
    GameSettings,

    /// <summary>Adds the value to its attribute's list.</summary>
    AddAttributeValue,
}

/// <summary>One row of the Problems panel.</summary>
/// <param name="Diagnostic">The validator's finding.</param>
/// <param name="Action">What its button does; <see cref="StudioProblemAction.None"/> for no button.</param>
/// <param name="ActionText">The button's words, such as "Add a picture". Empty when there is no button.</param>
public sealed record StudioProblemViewModel(PackDiagnostic Diagnostic, StudioProblemAction Action, string ActionText)
{
    /// <summary>Whether it blocks export.</summary>
    public bool IsError => Diagnostic.Severity == Xxsm.Core.Diagnostics.DiagnosticSeverity.Error;

    /// <summary>The sentence.</summary>
    public string Message => Diagnostic.Message;

    /// <summary>The character or thing it is about, or null for the game as a whole.</summary>
    public string? Subject => Diagnostic.Subject;

    /// <summary>What it is about in detail: the characters, field, hash or attribute value.</summary>
    public PackDiagnosticTarget? Target => Diagnostic.Target;

    /// <summary>Whether it has a button.</summary>
    public bool HasAction => Action != StudioProblemAction.None;

    /// <summary>Which of <see cref="Xxsm.Packs.Studio.PackProblemKinds"/> it is, for the panel's filter.</summary>
    public string Kind { get; } = Xxsm.Packs.Studio.PackProblemKinds.Of(Diagnostic.Code);
}

/// <summary>A hash in the shared-hashes panel, with who carries it.</summary>
/// <param name="Hash">The hash.</param>
/// <param name="CarriersText">The characters that carry it, by name.</param>
public sealed record StudioSharedHashRowViewModel(string Hash, string CarriersText);

/// <summary>A character in the shared-hashes panel.</summary>
/// <param name="InternalName">The character.</param>
/// <param name="DisplayName">Its name, as a sentence would give it.</param>
/// <param name="CarriesText">"carries 28 of them".</param>
public sealed record StudioSharingCharacterViewModel(string InternalName, string DisplayName, string CarriesText);

/// <summary>A hash the hashes editor marks for a look, and why.</summary>
/// <param name="Hash">The hash, as the character's list has it.</param>
/// <param name="Reason">Why it is marked.</param>
public sealed record StudioMarkedHashViewModel(string Hash, string Reason)
{
    /// <summary>Whether the pack ignores it, so the button offers to stop ignoring rather than remove it.</summary>
    public bool IsIgnored { get; init; }
}

/// <summary>An outfit in the "which is the default?" question.</summary>
/// <param name="InternalName">The outfit.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="IsDefault">Whether it is marked as the default now.</param>
public sealed record StudioDefaultChoiceViewModel(string InternalName, string DisplayName, bool IsDefault);

/// <summary>A request from the page for the table to start editing one cell.</summary>
/// <param name="Row">The row.</param>
/// <param name="Field">A <see cref="PackDiagnosticFields"/> value naming the column.</param>
/// <param name="Attribute">The attribute, for an attribute column.</param>
public sealed record StudioCellEditRequest(StudioCharacterRowViewModel Row, string Field, string? Attribute);

/// <summary>One filter chip in the Problems panel: a kind of problem, and how many there are.</summary>
public sealed partial class StudioProblemKindViewModel : ObservableObject
{
    private readonly Action<StudioProblemKindViewModel> _toggled;
    private bool _quiet;

    /// <summary>Creates the chip.</summary>
    /// <param name="id">The kind, one of <see cref="Xxsm.Packs.Studio.PackProblemKinds"/>.</param>
    /// <param name="label">What the chip says.</param>
    /// <param name="count">How many problems of this kind there are.</param>
    /// <param name="isSelected">Whether it starts picked.</param>
    /// <param name="toggled">Called whenever someone picks it or unpicks it.</param>
    public StudioProblemKindViewModel(
        string id, string label, int count, bool isSelected, Action<StudioProblemKindViewModel> toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);

        Id = id;
        Label = label;
        Count = count;
        _isSelected = isSelected;
        _toggled = toggled;
    }

    /// <summary>The kind.</summary>
    public string Id { get; }

    /// <summary>What the chip says, such as "No hashes".</summary>
    public string Label { get; }

    /// <summary>How many problems of this kind there are.</summary>
    public int Count { get; }

    /// <summary>The chip's text: its label and its count.</summary>
    public string Text => $"{Label}  {Count.ToString(System.Globalization.CultureInfo.CurrentCulture)}";

    /// <summary>Whether the panel is showing this kind.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Changes <see cref="IsSelected"/> without telling the page, for clearing every chip at once.</summary>
    public void SetSelected(bool selected)
    {
        _quiet = true;
        IsSelected = selected;
        _quiet = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_quiet)
        {
            _toggled(this);
        }
    }
}

/// <summary>One mod in the Try it panel: where it would go, and why.</summary>
public sealed record StudioTryRowViewModel(SortRunRow Row)
{
    /// <summary>The mod's folder name, as it is on disk.</summary>
    public string FolderName => Row.Mod.FolderName;

    /// <summary>The character folder it would end up in.</summary>
    public string Destination => Row.DestinationFolderName;

    /// <summary>Why, in words.</summary>
    public string Reason => Row.Reason;

    /// <summary>Whether a sort would move it.</summary>
    public bool WillMove => Row.WillMove;
}

/// <summary>What the last export wrote.</summary>
/// <param name="PackFile">The pack zip.</param>
/// <param name="Summary">"Version 2026.09.15, 1.2 MB, 14 warnings".</param>
/// <param name="Sha256">The zip's checksum.</param>
/// <param name="IndexPath">The registry index it was added to, or null.</param>
/// <param name="ContributionDirectory">The contribution folder written, or null.</param>
public sealed record StudioExportOutcome(string PackFile, string Summary, string Sha256, string? IndexPath, string? ContributionDirectory)
{
    /// <summary>Whether it was added to a registry.</summary>
    public bool HasIndex => IndexPath is not null;

    /// <summary>Whether a contribution folder was written.</summary>
    public bool HasContribution => ContributionDirectory is not null;
}
