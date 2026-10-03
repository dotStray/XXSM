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
using Xxsm.Packs.Pictures;
using Xxsm.Packs.Portraits;
using Xxsm.Packs.Sorting;

namespace Xxsm.Desktop.ViewModels;

/// <summary>Somewhere besides the selected game the Character Manager can edit in: a Pack Studio draft.</summary>
public interface ICharacterEditTarget
{
    /// <summary>What the character is read from, and what its outfit and hash choices come from.</summary>
    GameData Data { get; }

    /// <summary>Writes what the editor holds.</summary>
    /// <param name="editor">The editor, open on a character of <see cref="Data"/>.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The change was refused; the reason is the message.</exception>
    Task<IReadOnlyList<string>> SaveAsync(CharacterEditorViewModel editor, CancellationToken cancellationToken);

    /// <summary>The hashes being ignored where the character lives, to mark in the hash editor; often none.</summary>
    IReadOnlySet<string> IgnoredHashes => System.Collections.Immutable.ImmutableHashSet<string>.Empty;

    /// <summary>Takes one hash off that list, so the sorter scores it again.</summary>
    /// <returns>True when it was taken off; false when there is no list to take it off.</returns>
    bool StopIgnoring(string hash) => false;
}

/// <summary>The Character Manager: the editor panel, and the question before a custom character is deleted.</summary>
/// <remarks>Never activated: its work runs to completion rather than being cancelled with a page.</remarks>
public sealed partial class CharacterManagerViewModel(
    GameContext game,
    ICharacterEditor characters,
    IModHashLearner learner,
    IStoragePicker picker,
    IClipboardImageReader clipboard,
    ICharacterPortraitStore portraitStore,
    IPictureDownloader downloader,
    IPortraitCache portraits,
    INotificationService notifications,
    ViewModelWorkRunner runner,
    ITextCatalogue text,
    ILogger logger,
    Func<CancellationToken, Task> rescan,
    Func<CancellationToken, Task> reload,
    IModThumbnailCache thumbnails) : ViewModelBase
{
    private readonly GameContext _game = game;
    private readonly ICharacterEditor _characters = characters;
    private readonly IModHashLearner _learner = learner;
    private readonly IStoragePicker _picker = picker;
    private readonly IClipboardImageReader _clipboard = clipboard;
    private readonly ICharacterPortraitStore _portraitStore = portraitStore;
    private readonly IPictureDownloader _downloader = downloader;
    private readonly IPortraitCache _portraits = portraits;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly ILogger _logger = logger.ForContext<CharacterManagerViewModel>();
    private readonly Func<CancellationToken, Task> _rescan = rescan;
    private readonly Func<CancellationToken, Task> _reload = reload;

    /// <summary>Where the open editor writes, when it is not the selected game's overlay.</summary>
    private ICharacterEditTarget? _target;

    private ITextCatalogue Text { get; } = text;

    /// <summary>What a failure or a recorded conflict is titled: the page it has always belonged to.</summary>
    private string Heading => Text[nameof(Strings.Characters_Heading)];

    /// <summary>The editor panel, or null when it is closed.</summary>
    [ObservableProperty]
    private CharacterEditorViewModel? _editor;

    /// <summary>Whether the editor panel is open.</summary>
    public bool IsEditorOpen => Editor is not null;

    /// <summary>Whether the editor panel is on screen; the delete question and the mod picker replace it rather than
    /// stacking.</summary>
    public bool IsEditorShowing => IsEditorOpen && !IsDeleting && !IsPicking;

    /// <summary>The mods <em>Learn from a mod…</em> chooses from, in the editor's place.</summary>
    public ModPickerViewModel Picker => _modPicker ??= CreatePicker();

    private ModPickerViewModel? _modPicker;

    /// <summary>Whether the mod picker is showing.</summary>
    public bool IsPicking => _modPicker?.IsOpen == true;

    private ModPickerViewModel CreatePicker()
    {
        var created = new ModPickerViewModel(Text, thumbnails);
        created.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModPickerViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(IsPicking));
                OnPropertyChanged(nameof(IsEditorShowing));
            }
        };

        return created;
    }

    /// <summary>What the delete panel is asking about, or null when it is closed.</summary>
    [ObservableProperty]
    private CharacterDeleteViewModel? _deleting;

    /// <summary>Whether the delete panel is showing.</summary>
    public bool IsDeleting => Deleting is not null;

    /// <summary>Whether the editor or the delete question is on screen.</summary>
    public bool IsOpen => IsEditorOpen || IsDeleting;

    /// <summary>Whether the open editor writes somewhere other than the selected game — a Studio draft.</summary>
    public bool IsEditingElsewhere => _target is not null;

    /// <summary>The game data the open editor reads from.</summary>
    private GameData? Data => _target?.Data ?? _game.Data;

    /// <summary>Opens the editor for a new character. Only a name is required.</summary>
    [RelayCommand]
    private void NewCharacter() =>
        OpenEditor(null, new CharacterEditorViewModel(Text, _game.Data, suggestedName: null, _portraits, _logger));

    /// <summary>Opens the editor for a new character with its name already typed in.</summary>
    /// <param name="suggestedName">The name to start from — a tag the import did not know.</param>
    public void NewCharacter(string suggestedName) =>
        OpenEditor(null, new CharacterEditorViewModel(Text, _game.Data, suggestedName, _portraits, _logger));

    /// <summary>Opens the editor for an existing character. On the tile's context menu.</summary>
    [RelayCommand]
    private void EditCharacter(CharacterTileViewModel? tile)
    {
        if (tile?.Variant is { } variant)
        {
            Edit(variant);
        }
    }

    /// <summary>Opens the editor for a character or one of its skins, from the detail view's menu.</summary>
    /// <param name="variant">The character or skin to edit.</param>
    public void Edit(MergedVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);

        var editor = new CharacterEditorViewModel(Text, _game.Data, variant, _portraits, _logger);
        OpenEditor(null, editor);
        Track(editor.LoadPortraitAsync());
    }

    /// <summary>Opens the editor on a character in a Pack Studio draft; saving writes there, not to the game.</summary>
    /// <param name="target">Where the character is read from and written to.</param>
    /// <param name="internalName">The character.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The target has no such character.</exception>
    public void Edit(ICharacterEditTarget target, string internalName)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);

        var variant = target.Data.Variants.FirstOrDefault(v =>
                          string.Equals(v.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new Xxsm.Core.ModOperationException(
                          $"'{internalName}' cannot be opened in the Character Manager until its internal name is fixed.");

        var editor = new CharacterEditorViewModel(Text, target.Data, variant, _portraits, _logger);
        OpenEditor(target, editor);
        Track(editor.LoadPortraitAsync());
    }

    /// <summary>Opens the editor for a new character in a Pack Studio draft, with <em>More</em> showing.</summary>
    /// <param name="target">Where the character is written, and what its outfit choices come from.</param>
    /// <param name="suggestedName">The name typed so far, or empty.</param>
    public void NewCharacter(ICharacterEditTarget target, string suggestedName)
    {
        ArgumentNullException.ThrowIfNull(target);

        OpenEditor(target, new CharacterEditorViewModel(Text, target.Data, suggestedName, _portraits, _logger)
        {
            IsExpanded = true,
        });
    }

    private void OpenEditor(ICharacterEditTarget? target, CharacterEditorViewModel editor)
    {
        _target = target;
        Editor = editor;
        OnPropertyChanged(nameof(IsEditingElsewhere));
    }

    /// <summary>Closes the editor and the delete question without saving, as the page under it leaves.</summary>
    public void Close() => Editor = null;

    /// <summary>Closes the editor without saving.</summary>
    [RelayCommand]
    private void CancelEdit() => Editor = null;

    /// <summary>Writes whatever the editor holds, then reloads the grid.</summary>
    [RelayCommand]
    private Task SaveEditAsync()
    {
        if (Editor is not { CanSave: true } editor)
        {
            return Task.CompletedTask;
        }

        if (_target is { } target)
        {
            return _runner.RunAsync(
                Heading,
                async ct =>
                {
                    var saved = await target.SaveAsync(editor, ct).ConfigureAwait(true);

                    // Only the editor that was saved is closed: one opened since stays.
                    if (ReferenceEquals(Editor, editor))
                    {
                        Editor = null;
                    }

                    foreach (var note in saved.Distinct(StringComparer.Ordinal))
                    {
                        _notifications.Add(NotificationSeverity.Information, Heading, note);
                    }
                },
                CancellationToken.None);
        }

        if (_game.Data is not { } data)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var notes = new List<string>();

                // A picture given in the editor is copied in only now, so a cancel leaves nothing.
                if (editor.PendingPortrait is { } picture)
                {
                    editor.MarkPortraitStored(
                        await _portraitStore.StoreAsync(data.GameId, picture, ct).ConfigureAwait(true));
                }

                if (editor.IsNew)
                {
                    var created = await _characters.CreateAsync(data, editor.ToNewCharacter(), ct)
                        .ConfigureAwait(true);

                    notes.AddRange(created.Diagnostics.Select(diagnostic => diagnostic.Message));
                }
                else
                {
                    var edit = editor.ToEdit();

                    if (!edit.IsEmpty)
                    {
                        var edited = await _characters
                            .EditAsync(data, editor.Existing!.InternalName, edit, ct).ConfigureAwait(true);

                        notes.AddRange(edited.Diagnostics.Select(diagnostic => diagnostic.Message));
                    }

                    // The editor's list is the whole truth: a hash taken out of it is taken out of the variant too.
                    if (editor.RemovedHashes() is { Count: > 0 } removed)
                    {
                        await _characters
                            .RemoveHashesAsync(data, editor.Existing!.InternalName, removed, ct)
                            .ConfigureAwait(true);
                    }

                    if (editor.AddedHashes() is { Count: > 0 } added)
                    {
                        var result = await _characters
                            .AddHashesAsync(data, editor.Existing!.InternalName, added, ct).ConfigureAwait(true);

                        notes.AddRange(result.Diagnostics.Select(diagnostic => diagnostic.Message));
                    }
                }

                Editor = null;
                await _reload(ct).ConfigureAwait(true);

                foreach (var note in notes.Distinct(StringComparer.Ordinal))
                {
                    _notifications.Add(NotificationSeverity.Information, Heading, note);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Chooses a portrait for the character being edited.</summary>
    [RelayCommand]
    private Task ChoosePortraitAsync()
    {
        if (Editor is not { } editor)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var chosen = await _picker.PickFileAsync(
                        Text[nameof(Strings.CharacterEditor_Portrait_PickerTitle)],
                        [
                            new FileTypeFilter(
                                Text[nameof(Strings.CharacterEditor_Portrait_FileType)],
                                IModPreviewEditor.SupportedExtensions),
                        ],
                        cancellationToken: ct)
                    .ConfigureAwait(true);

                if (chosen is { Length: > 0 })
                {
                    await editor.UsePortraitAsync(PreviewImageSource.FromFile(chosen)).ConfigureAwait(true);
                }
            },
            CancellationToken.None);
    }

    /// <summary>Uses the clipboard's picture as the portrait: Ctrl+V on the picture, or its menu.</summary>
    [RelayCommand]
    private Task PastePortraitAsync()
    {
        if (Editor is not { } editor)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                if (await _clipboard.ReadAsync(ct).ConfigureAwait(true) is not { } image)
                {
                    editor.PortraitError = Text[nameof(Strings.ModImage_NothingToPaste)];
                    return;
                }

                await editor.UsePortraitAsync(image).ConfigureAwait(true);
            },
            CancellationToken.None);
    }

    /// <summary>Uses an image file dropped on the open editor as the portrait.</summary>
    /// <returns>A task that completes when the picture is showing, or has been refused.</returns>
    public Task DropPortraitAsync(string path) =>
        string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : DropPortraitAsync(PictureDrop.FromFile(path));

    /// <summary>What a drop carried as a picture: the file, or one dragged from a web page, fetched.</summary>
    /// <exception cref="ModOperationException">The drop is not a picture, or it could not be fetched.</exception>
    public Task<PreviewImageSource> ReadDropAsync(PictureDrop drop, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drop);

        return drop.ToSourceAsync(_downloader, Text, cancellationToken);
    }

    /// <summary>Uses a picture dropped on the open editor as the portrait, fetching a web one first.</summary>
    /// <returns>A task that completes when the picture is showing, or has been refused.</returns>
    public Task DropPortraitAsync(PictureDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);

        if (Editor is not { } editor)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                PreviewImageSource picture;

                try
                {
                    picture = await drop.ToSourceAsync(_downloader, Text, ct).ConfigureAwait(true);
                }
                catch (ModOperationException exception)
                {
                    editor.PortraitError = exception.Message;
                    return;
                }

                await editor.UsePortraitAsync(picture).ConfigureAwait(true);
            },
            CancellationToken.None);
    }

    /// <summary>The character's hashes as text, the same editor Studio's table opens.</summary>
    public HashListEditorViewModel HashList { get; } = new HashListEditorViewModel(text);

    /// <summary>Opens the whole hash list in a box, one per line, so twenty of them can be fixed at once.</summary>
    [RelayCommand]
    private void OpenHashList()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        IReadOnlySet<string> ignored = _target?.IgnoredHashes ?? System.Collections.Immutable.ImmutableHashSet<string>.Empty;

        HashList.Open(
            Text.Format(nameof(Strings.Studio_Hashes_Title), editor.Name),
            editor.Hashes.Select(row => row.Hash),
            text => ReadHashList(editor, text),
            [
                .. editor.Hashes
                    .Where(row => ignored.Contains(row.Hash))
                    .Select(row => new StudioMarkedHashViewModel(row.Hash, Text[nameof(Strings.Studio_Hashes_Mark_Ignored)])
                    {
                        IsIgnored = true,
                    }),
            ],
            _target is { } target ? target.StopIgnoring : null);
    }

    /// <summary>Makes the box the editor's whole hash list; the character changes only when the editor saves.</summary>
    /// <returns>True when the box was taken, so the editor can close.</returns>
    private bool ReadHashList(CharacterEditorViewModel editor, string text)
    {
        var parsed = HashPaste.Parse(text);

        // Text that holds no hash is a mistake, not a request to remove them all.
        if (parsed.IsEmpty && !string.IsNullOrWhiteSpace(text))
        {
            editor.HashPasteSummary = parsed.Rejected.Count > 0
                ? Text.Format(nameof(Strings.Studio_Cell_NotAHash), parsed.Rejected[0].Text, parsed.Rejected[0].Reason)
                : Text[nameof(Strings.HashList_NoHashes)];

            return false;
        }

        editor.ReplaceHashes(parsed.Entries);
        editor.HashPasteSummary = Text.Format(nameof(Strings.CharacterEditor_Hashes_Count), Text.Hashes(editor.Hashes.Count));

        return true;
    }

    /// <summary>Shows every mod in the Mods folder to learn hashes from, the edited character's own first.</summary>
    [RelayCommand]
    private Task LearnFromModAsync()
    {
        if (Editor is not { } editor || Data is not { } data)
        {
            return Task.CompletedTask;
        }

        var own = editor.Existing?.ModFilesName;

        List<(InstalledMod Mod, string? Character)> candidates =
        [
            .. (_game.Inventory?.AllMods ?? [])
                .Select(mod => (Mod: mod, Character: UnsortedMods.CharacterFor(mod.VariantFolderName, data)?.DisplayName))
                .OrderBy(pair => own is not null && PathComparer.AreNamesEqual(pair.Mod.VariantFolderName, own) ? 0 : 1)
                .ThenBy(pair => pair.Character ?? pair.Mod.VariantFolderName ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(pair => pair.Mod.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];

        return Picker.OpenAsync(new ModPickerRequest(
            Text.Format(nameof(Strings.CharacterEditor_Learn_Heading), editor.Name.Trim().Length > 0 ? editor.Name.Trim() : Text[nameof(Strings.CharacterEditor_Learn_ThisCharacter)]),
            Text[nameof(Strings.CharacterEditor_Learn_Body)],
            Text[nameof(Strings.CharacterEditor_Learn_NoMods)],
            SingleChoice: true,
            _ => Text[nameof(Strings.CharacterEditor_Learn_Confirm)],
            candidates,
            picked => LearnFromAsync(editor, data, picked[0].Path),
            Text[nameof(Strings.CharacterEditor_Learn_Outside)],
            () => LearnFromOutsideAsync(editor, data)));
    }

    /// <summary>Asks for a mod folder anywhere, and learns from it.</summary>
    private Task<bool> LearnFromOutsideAsync(CharacterEditorViewModel editor, GameData data) =>
        _runner.RunAsync(
            Heading,
            async ct =>
            {
                var folder = await _picker
                    .PickFolderAsync(Text[nameof(Strings.CharacterEditor_Learn_PickerTitle)], cancellationToken: ct)
                    .ConfigureAwait(true);

                if (folder is { Length: > 0 })
                {
                    await LearnAsync(editor, data, folder, ct).ConfigureAwait(true);
                }
            },
            CancellationToken.None);

    /// <summary>Reads every hash out of a mod folder into the editor's list.</summary>
    private Task<bool> LearnFromAsync(CharacterEditorViewModel editor, GameData data, string folder) =>
        _runner.RunAsync(Heading, ct => LearnAsync(editor, data, folder, ct), CancellationToken.None);

    private async Task LearnAsync(CharacterEditorViewModel editor, GameData data, string folder, CancellationToken cancellationToken)
    {
        var learned = await _learner.LearnAsync(folder, data, default, null, cancellationToken).ConfigureAwait(true);

        if (!learned.HasHashes)
        {
            editor.HashPasteSummary = Text[nameof(Strings.CharacterEditor_Learn_Nothing)];
            return;
        }

        var added = editor.Add([.. learned.Hashes.Select(hash => hash.Entry)]);

        editor.HashPasteSummary = Text.Format(
            nameof(Strings.CharacterEditor_Learn_Found),
            Text.Hashes(added),
            Path.GetFileName(PathComparer.Normalize(folder).TrimEnd('/')));

        if (editor.IsNew && editor.Name.Trim().Length == 0)
        {
            editor.Name = learned.SuggestedName;
        }
    }

    /// <summary>Asks where a custom character's mods should go before deleting it.</summary>
    [RelayCommand]
    private void StartDelete()
    {
        if (_target is null && Editor is { Existing: { } variant, CanDelete: true } && _game.Data is { } data)
        {
            Deleting = new CharacterDeleteViewModel(Text, data, variant, ModCountOf(variant));
        }
    }

    /// <summary>Backs out of deleting a character.</summary>
    [RelayCommand]
    private void CancelDelete() => Deleting = null;

    /// <summary>Deletes the character, re-homing its mods where the panel says.</summary>
    [RelayCommand]
    private Task ConfirmDeleteAsync()
    {
        if (Deleting is not { } deleting
            || _game.Data is not { } data
            || _game.ModsDirectory is not { Length: > 0 } modsDirectory)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                var result = await _characters
                    .DeleteAsync(data, deleting.Variant.InternalName, modsDirectory, deleting.RehomeTo, ct)
                    .ConfigureAwait(true);

                Deleting = null;
                Editor = null;

                await _reload(ct).ConfigureAwait(true);
                await _rescan(ct).ConfigureAwait(true);

                var name = deleting.Variant.DisplayName;
                var message = result.RehomedTo is { Length: > 0 } destination
                    ? Text.Format(
                        nameof(Strings.CharacterDelete_Done_Moved), name, Text.Mods(result.RehomedMods.Count), destination)
                    : Text.Format(nameof(Strings.CharacterDelete_Done), name);

                foreach (var diagnostic in result.Diagnostics)
                {
                    _notifications.Add(NotificationSeverity.Information, name, diagnostic.Message);
                }

                if (result.Deletion is not { } deletion)
                {
                    _notifications.Add(NotificationSeverity.Information, name, message);
                    return;
                }

                // The undo offer lives on the notice, which outlives the delete question.
                var offer = new DeletionOffer(deletion, result.RestoreRecordPath);
                Notification? notice = null;

                var undo = new AsyncRelayCommand(async () =>
                {
                    await RestoreCharacterAsync(offer).ConfigureAwait(true);

                    // Spent once everything is back; otherwise the button stays for the rest.
                    if (offer.Done && notice is not null)
                    {
                        _notifications.Dismiss(notice);
                    }
                });

                notice = _notifications.Add(
                    NotificationSeverity.Information,
                    name,
                    message,
                    action: undo,
                    actionText: Text[nameof(Strings.Notifications_Undo)]);
            },
            CancellationToken.None);
    }

    /// <summary>Undoes a character deletion from its notice; not cancelled with the page.</summary>
    private Task<bool> RestoreCharacterAsync(DeletionOffer offer) => _runner.RunAsync(
        Text[nameof(Strings.Notifications_Undo)],
        async ct =>
        {
            if (_game.Data is not { } data)
            {
                return;
            }

            var restored = await _characters
                .RestoreDeletedAsync(data, offer.Deletion, offer.RecordPath, ct)
                .ConfigureAwait(true);

            offer.Deletion = restored.Remaining;
            offer.Done = restored.IsComplete;

            await _reload(ct).ConfigureAwait(true);
            await _rescan(ct).ConfigureAwait(true);

            var name = restored.DisplayName;

            _notifications.Add(
                restored.IsComplete ? NotificationSeverity.Information : NotificationSeverity.Warning,
                Text[nameof(Strings.Notifications_Undo)],
                !restored.IsComplete
                    ? Text.ForCount(restored.Skipped.Count, nameof(Strings.CharacterDelete_Restored_WithSkips_One), nameof(Strings.CharacterDelete_Restored_WithSkips), name, Text.Mods(restored.Skipped.Count))
                    : restored.RestoredMods.Count > 0
                        ? Text.Format(nameof(Strings.CharacterDelete_Restored_Mods), name, Text.Mods(restored.RestoredMods.Count))
                        : Text.Format(nameof(Strings.CharacterDelete_Restored), name));

            foreach (var skip in restored.Skipped)
            {
                _notifications.Add(NotificationSeverity.Warning, skip.Path, skip.Reason);
            }

            foreach (var diagnostic in restored.Diagnostics)
            {
                _notifications.Add(NotificationSeverity.Information, name, diagnostic.Message);
            }
        },
        CancellationToken.None);

    /// <summary>A deletion's undo, as it stands after each press.</summary>
    private sealed class DeletionOffer(CharacterDeletion deletion, string? recordPath)
    {
        public CharacterDeletion Deletion { get; set; } = deletion;

        public string? RecordPath { get; } = recordPath;

        public bool Done { get; set; }
    }

    /// <summary>Discards every edit to a pack character, putting it back as the pack has it.</summary>
    [RelayCommand]
    private Task ResetCharacterAsync()
    {
        if (_target is not null || Editor is not { Existing: { } variant, CanReset: true } || _game.Data is not { } data)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                await _characters.ResetAsync(data, variant.InternalName, fields: null, ct).ConfigureAwait(true);

                Editor = null;
                await _reload(ct).ConfigureAwait(true);
            },
            CancellationToken.None);
    }

    /// <summary>Lets future pack updates change a character the user has edited.</summary>
    [RelayCommand]
    private Task UnlockCharacterAsync()
    {
        if (_target is not null || Editor is not { Existing: { } variant, CanUnlock: true } || _game.Data is not { } data)
        {
            return Task.CompletedTask;
        }

        return _runner.RunAsync(
            Heading,
            async ct =>
            {
                await _characters.UnlockAsync(data, variant.InternalName, fields: null, ct).ConfigureAwait(true);

                Editor = null;
                await _reload(ct).ConfigureAwait(true);
            },
            CancellationToken.None);
    }

    private int ModCountOf(MergedVariant variant)
    {
        if (_game.Inventory is not { } inventory)
        {
            return 0;
        }

        return inventory.VariantFolders
            .Where(folder => PathComparer.AreNamesEqual(folder.Name, variant.ModFilesName))
            .Sum(folder => folder.Mods.Count);
    }

    partial void OnEditorChanged(CharacterEditorViewModel? value)
    {
        _modPicker?.Cancel();

        OnPropertyChanged(nameof(IsEditorOpen));
        OnPropertyChanged(nameof(IsEditorShowing));
        OnPropertyChanged(nameof(IsOpen));

        if (value is null)
        {
            Deleting = null;
            _target = null;
            OnPropertyChanged(nameof(IsEditingElsewhere));
        }
    }

    partial void OnEditorChanged(CharacterEditorViewModel? oldValue, CharacterEditorViewModel? newValue) =>
        oldValue?.Dispose();

    partial void OnDeletingChanged(CharacterDeleteViewModel? value)
    {
        OnPropertyChanged(nameof(IsDeleting));
        OnPropertyChanged(nameof(IsEditorShowing));
        OnPropertyChanged(nameof(IsOpen));
    }
}
