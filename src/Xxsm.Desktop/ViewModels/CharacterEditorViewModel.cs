using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One hash shown in the editor's hash list.</summary>
/// <param name="Hash">The hash, lowercase.</param>
/// <param name="Kind">Which buffer or texture it is.</param>
public sealed record HashRowViewModel(string Hash, HashKind Kind)
{
    /// <summary>The kind as a label — <c>ib</c>, <c>position_vb</c>, <c>unknown</c>.</summary>
    public string KindText => Kind switch
    {
        HashKind.Ib => "ib",
        HashKind.PositionVb => "position_vb",
        HashKind.BlendVb => "blend_vb",
        HashKind.TexcoordVb => "texcoord_vb",
        HashKind.DrawVb => "draw_vb",
        HashKind.RootVs => "root_vs",
        HashKind.Texture => "texture",
        _ => "unknown",
    };
}

/// <summary>A character offered as somewhere to move mods to, or as a base character.</summary>
/// <param name="InternalName">The variant's id.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="ModFilesName">The folder its mods live in, when a path must be named; otherwise null.</param>
public sealed record CharacterChoiceViewModel(
    string InternalName, string DisplayName, string? ModFilesName = null);

/// <summary>The Character Manager's panel for creating or editing a character; only the name is required.</summary>
public sealed partial class CharacterEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>Twice the editor picture's 132px, for a sharp picture on a high-density screen.</summary>
    private const int DecodeWidthPixels = 264;

    private readonly ITextCatalogue _text;
    private readonly MergedVariant? _existing;
    private readonly GameData? _data;
    private readonly IPortraitCache? _portraits;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _loadCts = new();

    private IBitmapLease? _portraitHandle;
    private Bitmap? _ownedBitmap;
    private bool _disposed;

    /// <summary>Creates the editor for a new character.</summary>
    /// <param name="text">The interface's wording.</param>
    /// <param name="data">The merged game data, for the characters that may be a base.</param>
    /// <param name="suggestedName">A name to start with, from a mod folder, or null.</param>
    /// <param name="portraits">Decodes a character's existing portrait, or null to show none.</param>
    /// <param name="logger">Where a picture that would not decode is logged in full.</param>
    public CharacterEditorViewModel(
        ITextCatalogue text,
        GameData? data,
        string? suggestedName = null,
        IPortraitCache? portraits = null,
        ILogger? logger = null)
        : this(text, data, existing: null, portraits, logger)
    {
        Name = suggestedName ?? string.Empty;
    }

    /// <summary>Creates the editor for an existing character.</summary>
    /// <param name="text">The interface's wording.</param>
    /// <param name="data">The merged game data, for the characters that may be a base.</param>
    /// <param name="existing">The variant to edit, or null to create one.</param>
    /// <param name="portraits">Decodes the character's existing portrait, or null to show none.</param>
    /// <param name="logger">Where a picture that would not decode is logged in full.</param>
    public CharacterEditorViewModel(
        ITextCatalogue text,
        GameData? data,
        MergedVariant? existing,
        IPortraitCache? portraits = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text;
        _existing = existing;
        _data = data;
        _portraits = portraits;
        _logger = (logger ?? Serilog.Core.Logger.None).ForContext<CharacterEditorViewModel>();

        foreach (var choice in (data?.VisibleVariants ?? [])
                 .Where(variant => existing is null
                                   || !string.Equals(
                                       variant.InternalName, existing.InternalName, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(variant => variant.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            BaseCharacterChoices.Add(new CharacterChoiceViewModel(choice.InternalName, choice.DisplayName));
        }

        if (existing is null)
        {
            _name = string.Empty;
            _internalName = string.Empty;
            _modFilesName = string.Empty;
            _aliases = string.Empty;
            _notes = string.Empty;
            return;
        }

        _name = existing.DisplayName;
        _internalName = existing.InternalName;
        _modFilesName = existing.ModFilesName;
        _aliases = string.Join(", ", existing.Aliases);
        _notes = existing.Notes ?? string.Empty;
        _imagePath = existing.Image;
        _hidden = existing.Hidden;

        _selectedBaseCharacter = existing.BaseCharacterId is { Length: > 0 } baseId
            ? BaseCharacterChoices.FirstOrDefault(choice =>
                string.Equals(choice.InternalName, baseId, StringComparison.OrdinalIgnoreCase))
            : null;

        foreach (var hash in existing.Hashes.OrderBy(hash => hash.Kind).ThenBy(hash => hash.Hash, StringComparer.Ordinal))
        {
            Hashes.Add(new HashRowViewModel(hash.Hash, hash.Kind));
        }
    }

    /// <summary>Whether this is creating a character rather than editing one.</summary>
    public bool IsNew => _existing is null;

    /// <summary>The variant being edited, or null while creating one.</summary>
    public MergedVariant? Existing => _existing;

    /// <summary>The panel's title.</summary>
    public string Heading => IsNew
        ? _text[nameof(Strings.CharacterEditor_New_Heading)]
        : _text.Format(nameof(Strings.CharacterEditor_Edit_Heading), _existing!.DisplayName);

    /// <summary>The label on the save button.</summary>
    public string SaveText => IsNew
        ? _text[nameof(Strings.CharacterEditor_Create)]
        : _text[nameof(Strings.CharacterEditor_Save)];

    /// <summary>The name the user typed. The only thing that is required.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>The id, prefilled from the name while it is being created and untouched.</summary>
    [ObservableProperty]
    private string _internalName;

    /// <summary>The Mods sub-folder name.</summary>
    [ObservableProperty]
    private string _modFilesName;

    /// <summary>The aliases the sorter accepts, comma-separated.</summary>
    [ObservableProperty]
    private string _aliases;

    /// <summary>Free-text notes.</summary>
    [ObservableProperty]
    private string _notes;

    /// <summary>The portrait, or null for the initials tile.</summary>
    [ObservableProperty]
    private string? _imagePath;

    /// <summary>The picture in the editor: the character's own, or one just given; null for none or not yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortrait))]
    private Bitmap? _portrait;

    /// <summary>Whether there is a picture to show.</summary>
    public bool HasPortrait => Portrait is not null;

    /// <summary>A picture given here and not yet stored; copied in on save, so cancelling leaves nothing.</summary>
    public PreviewImageSource? PendingPortrait { get; private set; }

    /// <summary>Why the last picture offered could not be used, or null.</summary>
    [ObservableProperty]
    private string? _portraitError;

    /// <summary>Whether there is a portrait to remove, saved or not yet saved.</summary>
    public bool CanRemovePortrait => ImagePath is not null || PendingPortrait is not null;

    /// <summary>Completes once the most recent picture has decoded, or been refused.</summary>
    public Task PictureReady { get; private set; } = Task.CompletedTask;

    /// <summary>Whether the character is hidden from the grid.</summary>
    [ObservableProperty]
    private bool _hidden;

    /// <summary>The character this is an outfit of, or null for a base character.</summary>
    [ObservableProperty]
    private CharacterChoiceViewModel? _selectedBaseCharacter;

    /// <summary>Whether the optional fields are showing.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>The field the view should put the caret in once showing, or null; the view clears it.</summary>
    [ObservableProperty]
    private string? _focusField;

    /// <summary>Asks for a field to be focused, opening the optional fields when it is one of them.</summary>
    /// <param name="field">The field: a display name, internal name or Mods folder name.</param>
    public void RequestFocus(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        if (field != Xxsm.Packs.Loading.PackDiagnosticFields.DisplayName)
        {
            IsExpanded = true;
        }

        FocusField = field;
    }

    /// <summary>Free text the user has pasted hashes into.</summary>
    /// <remarks>Not named <c>HashPaste</c> on purpose: that would silently shadow the parser class here.</remarks>
    [ObservableProperty]
    private string _pastedHashes = string.Empty;

    /// <summary>What the parser made of <see cref="PastedHashes"/>, or null before it was read.</summary>
    [ObservableProperty]
    private string? _hashPasteSummary;

    /// <summary>Whether the editor has anything to complain about, and what.</summary>
    [ObservableProperty]
    private string? _notice;

    /// <summary>The characters that could be this one's base.</summary>
    public ObservableCollection<CharacterChoiceViewModel> BaseCharacterChoices { get; } = [];

    /// <summary>The hashes the character will have when saved.</summary>
    public ObservableCollection<HashRowViewModel> Hashes { get; } = [];

    /// <summary>Whether there are any hashes to show.</summary>
    public bool HasHashes => Hashes.Count > 0;

    /// <summary>The line under the hash list — a count, or that there are none.</summary>
    public string HashesText => Hashes.Count == 0
        ? _text[nameof(Strings.CharacterEditor_Hashes_None)]
        : _text.Format(nameof(Strings.CharacterEditor_Hashes_Count), _text.Hashes(Hashes.Count));

    /// <summary>Whether the character can be deleted, which only a custom one can.</summary>
    public bool CanDelete => _existing is { Origin: VariantOrigin.Custom };

    /// <summary>Whether there is anything to reset, which there is once the user has edited it.</summary>
    public bool CanReset => _existing is { Origin: VariantOrigin.Modified };

    /// <summary>Whether the character is locked against pack updates, so unlocking is offered.</summary>
    public bool CanUnlock => _existing is { Origin: VariantOrigin.Modified, IsLocked: true };

    /// <summary>Whether the name box holds something that can be saved.</summary>
    public bool CanSave => Name.Trim().Length > 0;

    /// <summary>The id that would be used, derived from the name when the box is empty.</summary>
    public string EffectiveInternalName => InternalName.Trim() is { Length: > 0 } typed
        ? typed
        : CharacterNames.Slugify(Name);

    /// <summary>The folder mods will be filed under, for the line beneath the name box.</summary>
    public string FolderText => _text.Format(
        nameof(Strings.CharacterEditor_Folder),
        ModFilesName.Trim() is { Length: > 0 } typed ? typed : EffectiveInternalName);

    /// <summary>Whether there is a folder name worth showing under the name box, not the empty fallback.</summary>
    public bool HasFolderText => Name.Trim().Length > 0 || ModFilesName.Trim().Length > 0;

    /// <summary>Removes one hash from the list. Nothing is written until the editor is saved.</summary>
    [RelayCommand]
    private void RemoveHash(HashRowViewModel? row)
    {
        if (row is not null && Hashes.Remove(row))
        {
            RaiseHashesChanged();
        }
    }

    /// <summary>Clears the portrait, so the initials tile is drawn instead.</summary>
    [RelayCommand]
    private void ClearImage()
    {
        ImagePath = null;
        PendingPortrait = null;
        PortraitError = null;
        ReleasePortrait();
        OnPropertyChanged(nameof(CanRemovePortrait));
    }

    /// <summary>Shows the character's current portrait, if it has one. Safe to call and forget.</summary>
    public async Task LoadPortraitAsync()
    {
        if (_existing is null || _data is null || _portraits is null)
        {
            return;
        }

        IBitmapLease? handle;

        try
        {
            handle = await _portraits.AcquireAsync(_data, _existing, _loadCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Replaced or closed while decoding: give the lease straight back.
        if (_disposed || PendingPortrait is not null || !string.Equals(ImagePath, _existing.Image, StringComparison.Ordinal))
        {
            handle?.Dispose();
            return;
        }

        _portraitHandle = handle;
        Portrait = handle?.Bitmap;
    }

    /// <summary>Shows a picture the user gave and keeps it to store on save; a non-picture is refused here.</summary>
    /// <returns>A task that completes when the picture is showing, or has been refused.</returns>
    public Task UsePortraitAsync(PreviewImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        PictureReady = ShowPortraitAsync(image);
        return PictureReady;
    }

    /// <summary>Records where the picture given in the editor was stored, before the character is written.</summary>
    /// <param name="imageUrl">The stored copy, as a character's <c>image</c> holds it.</param>
    public void MarkPortraitStored(string imageUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageUrl);

        ImagePath = imageUrl;
        PendingPortrait = null;
        OnPropertyChanged(nameof(CanRemovePortrait));
    }

    /// <summary>Releases the picture the editor is showing.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loadCts.Cancel();
        _loadCts.Dispose();
        ReleasePortrait();
    }

    private async Task ShowPortraitAsync(PreviewImageSource image)
    {
        if (image.FilePath is { } file && !IModPreviewEditor.IsSupportedImage(file))
        {
            PortraitError = _text.Format(nameof(Strings.ModImage_Unsupported), PathDisplay.Show(file));
            return;
        }

        byte[] bytes;
        Bitmap decoded;

        try
        {
            bytes = await image.ReadAsync().ConfigureAwait(true);

            // Refused before decoding, from the header alone.
            if (PictureSize.TryRead(bytes, out var size) && size.IsTooLarge)
            {
                PortraitError = _text.Format(nameof(Strings.ModImage_TooLarge), size.Width, size.Height);
                return;
            }

            decoded = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(stream, DecodeWidthPixels, BitmapInterpolationMode.HighQuality);
            }).ConfigureAwait(true);
        }
        catch (ModOperationException exception)
        {
            PortraitError = exception.Message;
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The decoder's own text says nothing to a user: logged in full, and the editor says what it means.
            _logger.Warning(exception, "Could not decode the portrait {Source}", image.FilePath ?? "(clipboard)");
            PortraitError = _text[nameof(Strings.CharacterEditor_Portrait_Unreadable)];
            return;
        }

        if (_disposed)
        {
            decoded.Dispose();
            return;
        }

        ReleasePortrait();
        _ownedBitmap = decoded;
        Portrait = decoded;
        PendingPortrait = PreviewImageSource.FromBytes(bytes, image.Extension);
        PortraitError = null;
        OnPropertyChanged(nameof(CanRemovePortrait));
    }

    /// <summary>Takes the picture off screen, then lets go of it: a lease back, or its own bitmap disposed.</summary>
    private void ReleasePortrait()
    {
        var owned = _ownedBitmap;
        var handle = _portraitHandle;

        _ownedBitmap = null;
        _portraitHandle = null;

        // Cleared before disposing, so the Image never draws a disposed bitmap.
        Portrait = null;

        owned?.Dispose();
        handle?.Dispose();
    }

    /// <summary>Detaches the character from the family it is an outfit of.</summary>
    [RelayCommand]
    private void ClearBaseCharacter() => SelectedBaseCharacter = null;

    /// <summary>Reads the paste box, adds what it understood, and says so; an unknown kind is kept.</summary>
    [RelayCommand]
    private void ReadHashPaste()
    {
        var result = HashPaste.Parse(PastedHashes);

        if (result.IsEmpty && result.Rejected.Count == 0)
        {
            HashPasteSummary = _text[nameof(Strings.CharacterEditor_Paste_Nothing)];
            return;
        }

        var added = Add(result.Entries);

        HashPasteSummary = Describe(added, result.Hashes.Count - added, result.Rejected.Count);

        if (added > 0)
        {
            PastedHashes = string.Empty;
        }
    }

    /// <summary>Makes these the character's whole hash list, as the text editor saves; the paste box adds.</summary>
    public void ReplaceHashes(IReadOnlyList<PackHashEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Hashes.Clear();

        foreach (var entry in entries)
        {
            if (!Hashes.Any(row =>
                    string.Equals(row.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase)
                    && row.Kind == entry.Kind))
            {
                Hashes.Add(new HashRowViewModel(entry.Hash, entry.Kind));
            }
        }

        RaiseHashesChanged();
    }

    /// <summary>Adds hashes the editor was handed, from a paste or from a mod, skipping duplicates.</summary>
    public int Add(IReadOnlyList<PackHashEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var added = 0;

        foreach (var entry in entries)
        {
            if (Hashes.Any(row =>
                    string.Equals(row.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase)
                    && row.Kind == entry.Kind))
            {
                continue;
            }

            Hashes.Add(new HashRowViewModel(entry.Hash, entry.Kind));
            added++;
        }

        if (added > 0)
        {
            RaiseHashesChanged();
        }

        return added;
    }

    /// <summary>Says what a read of hashes produced, for the line under the paste box.</summary>
    /// <param name="added">How many were added.</param>
    /// <param name="duplicates">How many were already there.</param>
    /// <param name="rejected">How many were not hashes.</param>
    public string Describe(int added, int duplicates, int rejected)
    {
        var parts = new List<string>
        {
            _text.Format(nameof(Strings.CharacterEditor_Paste_Added), _text.Hashes(added)),
        };

        if (duplicates > 0)
        {
            parts.Add(_text.Format(nameof(Strings.CharacterEditor_Paste_Duplicates), duplicates));
        }

        if (rejected > 0)
        {
            parts.Add(_text.Format(nameof(Strings.CharacterEditor_Paste_Rejected), rejected));
        }

        return string.Join(" ", parts);
    }

    /// <summary>The hashes the editor holds, as entries for the Character Manager.</summary>
    public IReadOnlyList<PackHashEntry> HashEntries() =>
        [.. Hashes.Select(row => new PackHashEntry { Kind = row.Kind, Hash = row.Hash })];

    /// <summary>What creating this character asks for.</summary>
    public NewCharacter ToNewCharacter() => new()
    {
        DisplayName = Name.Trim(),
        InternalName = InternalName.Trim() is { Length: > 0 } typed ? typed : null,
        BaseCharacterId = SelectedBaseCharacter?.InternalName,
        ModFilesName = ModFilesName.Trim() is { Length: > 0 } folder ? folder : null,
        Image = ImagePath,
        Aliases = SplitAliases(),
        Notes = Notes.Trim() is { Length: > 0 } notes ? notes : null,
        Hidden = Hidden,
        Hashes = HashEntries(),
    };

    /// <summary>Only the fields that differ from the variant now, since each named field becomes an override.</summary>
    /// <returns>The edit. Empty when nothing was changed.</returns>
    public CharacterEdit ToEdit()
    {
        if (_existing is null)
        {
            return new CharacterEdit();
        }

        var aliases = SplitAliases();

        return new CharacterEdit
        {
            DisplayName = Changed(Name.Trim(), _existing.DisplayName)
                ? EditField<string>.To(Name.Trim())
                : EditField<string>.Unchanged,

            ModFilesName = Changed(ModFilesName.Trim(), _existing.ModFilesName)
                ? EditField<string>.To(ModFilesName.Trim())
                : EditField<string>.Unchanged,

            Notes = Changed(Notes.Trim(), _existing.Notes ?? string.Empty)
                ? EditField<string>.To(Notes.Trim() is { Length: > 0 } notes ? notes : null)
                : EditField<string>.Unchanged,

            Image = Changed(ImagePath ?? string.Empty, _existing.Image ?? string.Empty)
                ? EditField<string>.To(ImagePath)
                : EditField<string>.Unchanged,

            BaseCharacterId = Changed(
                SelectedBaseCharacter?.InternalName ?? string.Empty, _existing.BaseCharacterId ?? string.Empty)
                ? EditField<string>.To(SelectedBaseCharacter?.InternalName)
                : EditField<string>.Unchanged,

            Aliases = aliases.SequenceEqual(_existing.Aliases, StringComparer.Ordinal)
                ? EditField<IReadOnlyList<string>>.Unchanged
                : EditField<IReadOnlyList<string>>.To(aliases),

            Hidden = Hidden == _existing.Hidden
                ? EditField<bool>.Unchanged
                : EditField<bool>.To(Hidden),
        };
    }

    /// <summary>The hashes the user added that the variant does not already have.</summary>
    public IReadOnlyList<PackHashEntry> AddedHashes() =>
    [
        .. HashEntries().Where(entry => _existing is null
                                        || !_existing.Hashes.Any(existing => Same(existing, entry))),
    ];

    /// <summary>The hashes the variant has that the user removed from the list.</summary>
    public IReadOnlyList<string> RemovedHashes() =>
    [
        .. (_existing?.Hashes ?? [])
            .Where(existing => !Hashes.Any(row =>
                string.Equals(row.Hash, existing.Hash, StringComparison.OrdinalIgnoreCase)
                && row.Kind == existing.Kind))
            .Select(existing => existing.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private static bool Same(PackHashEntry left, PackHashEntry right) =>
        string.Equals(left.Hash, right.Hash, StringComparison.OrdinalIgnoreCase) && left.Kind == right.Kind;

    private static bool Changed(string typed, string current) => !string.Equals(typed, current, StringComparison.Ordinal);

    private IReadOnlyList<string> SplitAliases() =>
    [
        .. Aliases
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(EffectiveInternalName));
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(HasFolderText));
        Notice = null;
    }

    partial void OnInternalNameChanged(string value)
    {
        OnPropertyChanged(nameof(EffectiveInternalName));
        OnPropertyChanged(nameof(FolderText));
    }

    partial void OnImagePathChanged(string? value) => OnPropertyChanged(nameof(CanRemovePortrait));

    partial void OnModFilesNameChanged(string value)
    {
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(HasFolderText));
    }

    private void RaiseHashesChanged()
    {
        OnPropertyChanged(nameof(HasHashes));
        OnPropertyChanged(nameof(HashesText));
    }
}
