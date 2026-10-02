using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Mods;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Studio;

namespace Xxsm.Desktop.ViewModels.Pages;

/// <summary>The Problems panel's buttons: each does what fixes its problem, or shows what it is about.</summary>
public sealed partial class StudioPageViewModel
{
    /// <summary>Raised when a fix is typed into a table cell: the view starts editing that cell.</summary>
    public event EventHandler<StudioCellEditRequest>? CellEditRequested;

    // Which button a problem gets

    private StudioProblemViewModel ToProblem(PackDiagnostic problem, HashSet<string> names)
    {
        var target = problem.Target;
        var first = target is { Characters.Count: > 0 } about ? about.Characters[0] : null;
        var isRow = first is { } name && names.Contains(name);

        (StudioProblemAction, string) Button(StudioProblemAction action, string key) => (action, Text[key]);

        var (action, text) = problem.Code switch
        {
            PackValidationCodes.HashSharedBetweenFamilies when target is { Characters.Count: > 1 } =>
                Button(StudioProblemAction.SharedHashes, nameof(Strings.Studio_Fix_SharedHashes)),
            PackValidationCodes.HashFansOut when target?.Hash is not null =>
                Button(StudioProblemAction.HashCarriers, nameof(Strings.Studio_Fix_Carriers)),
            PackValidationCodes.PortraitMissing or PackValidationCodes.PortraitFileMissing when isRow =>
                Button(StudioProblemAction.Picture, nameof(Strings.Studio_Fix_AddPicture)),
            PackValidationCodes.PortraitTooLarge when isRow =>
                Button(StudioProblemAction.Picture, nameof(Strings.Studio_Fix_ReplacePicture)),
            PackValidationCodes.PortraitOutsidePack when isRow =>
                Button(StudioProblemAction.CopyPictureIn, nameof(Strings.Studio_Fix_CopyPicture)),
            PackValidationCodes.VariantWithoutDisplayName
                or PackValidationCodes.FolderNameUnusable
                or PackValidationCodes.FolderIsOthers
                or PackValidationCodes.FolderNameShared
                or PackValidationCodes.NamesIndistinct when isRow =>
                Button(StudioProblemAction.EditField, nameof(Strings.Studio_Fix_Edit)),
            PackDiagnosticCodes.InvalidInternalName or PackDiagnosticCodes.DuplicateInternalName when isRow =>
                Button(StudioProblemAction.EditCell, nameof(Strings.Studio_Fix_Edit)),
            PackDiagnosticCodes.SelfReferencingFamily
                or PackDiagnosticCodes.DanglingBaseCharacter
                or PackValidationCodes.OutfitOfOutfit when isRow =>
                Button(StudioProblemAction.EditCell, nameof(Strings.Studio_Fix_Outfit)),
            PackDiagnosticCodes.FamilyWithoutDefault or PackDiagnosticCodes.FamilyWithMultipleDefaults when isRow =>
                Button(StudioProblemAction.ChooseDefault, nameof(Strings.Studio_Fix_Default)),
            PackDiagnosticCodes.VariantWithoutHashes when isRow =>
                Button(StudioProblemAction.Hashes, nameof(Strings.Studio_Fix_AddHashes)),
            PackValidationCodes.HashMalformed when isRow =>
                Button(StudioProblemAction.Hashes, nameof(Strings.Studio_Fix_ShowHash)),
            PackDiagnosticCodes.HashForUnknownVariant when first is { Length: > 0 } && !isRow =>
                Button(StudioProblemAction.UnclaimedHashes, nameof(Strings.Studio_Fix_Unclaimed)),
            PackValidationCodes.IgnoredHashMalformed or PackValidationCodes.IgnoredHashUnused
                when target?.Hash is not null =>
                Button(StudioProblemAction.Unignore, nameof(Strings.Studio_Fix_Unignore)),
            PackValidationCodes.AttributeValueUndeclared when target is { Attribute: { } attribute, Value: { } value } =>
                (StudioProblemAction.AddAttributeValue,
                    Text.Format(nameof(Strings.Studio_Fix_AddValue), value, AttributeLabel(attribute))),
            PackValidationCodes.AttributeNotNumber when isRow && target?.Attribute is not null =>
                Button(StudioProblemAction.EditCell, nameof(Strings.Studio_Fix_Edit)),
            PackValidationCodes.AttributeUndeclared
                or PackValidationCodes.AttributeIdInvalid
                or PackValidationCodes.AttributeValueDuplicate
                or PackValidationCodes.GameWithoutName =>
                Button(StudioProblemAction.GameSettings, nameof(Strings.Studio_Fix_GameSettings)),

            // A game id and pack version come from the files; a character with no internal name has no row.
            PackValidationCodes.GameIdInvalid
                or PackDiagnosticCodes.GameIdMismatch
                or PackValidationCodes.PackVersionMissing
                or PackDiagnosticCodes.MissingInternalName => (StudioProblemAction.None, string.Empty),

            _ when problem.Subject is { } subject && names.Contains(subject) =>
                Button(StudioProblemAction.Select, nameof(Strings.Studio_Problem_Show)),
            _ => (StudioProblemAction.None, string.Empty),
        };

        return new StudioProblemViewModel(problem, action, text);
    }

    /// <summary>Does what a problem's button says.</summary>
    /// <returns>A task that completes when the fix is made or its panel is open.</returns>
    [RelayCommand]
    public Task ActOnProblemAsync(StudioProblemViewModel? problem)
    {
        if (problem is null || Session is null)
        {
            return Task.CompletedTask;
        }

        var target = problem.Target;
        var characters = target?.Characters ?? [];
        var first = characters.Count > 0 ? characters[0] : null;

        switch (problem.Action)
        {
            case StudioProblemAction.Select when problem.Subject is { } subject:
                Select(subject);
                break;

            case StudioProblemAction.SharedHashes:
                OpenSharing(characters);
                break;

            case StudioProblemAction.HashCarriers when target?.Hash is { } hash:
                OpenCarriers(hash);
                break;

            case StudioProblemAction.Picture when RowFor(first) is { } row:
                SelectAll([row.InternalName]);
                EditCharacter(row);
                CharacterManager.Editor?.RequestFocus(PackDiagnosticFields.Image);
                break;

            case StudioProblemAction.CopyPictureIn when RowFor(first) is { } row:
                SelectAll([row.InternalName]);
                return CopyPictureInAsync(row);

            case StudioProblemAction.EditField when RowFor(first) is { } row:
                SelectAll(characters);
                EditCharacter(row);
                CharacterManager.Editor?.RequestFocus(target!.Field ?? PackDiagnosticFields.DisplayName);
                break;

            case StudioProblemAction.EditCell when RowFor(problem.Subject ?? first) is { } row:
                SelectAll(target?.Field == PackDiagnosticFields.BaseCharacterId ? FamilyRows(characters) : characters);
                CellEditRequested?.Invoke(this, new StudioCellEditRequest(row, target!.Field!, target.Attribute));
                break;

            case StudioProblemAction.ChooseDefault:
                SelectAll(characters);
                OpenDefaultChoice(characters);
                break;

            case StudioProblemAction.Hashes when RowFor(first) is { } row:
                SelectAll([row.InternalName]);
                OpenHashes(row, target?.Hash is { } bad
                    ? [new StudioMarkedHashViewModel(bad, Text[nameof(Strings.Studio_Hashes_Mark_Malformed)])]
                    : []);
                break;

            case StudioProblemAction.UnclaimedHashes when first is not null:
                OpenUnclaimed(first);
                break;

            case StudioProblemAction.Unignore when target?.Hash is { } entry:
                Edit(draft => DraftEdits.UnignoreHashes(draft, [entry]).Draft);
                break;

            case StudioProblemAction.GameSettings:
                GameSettings.OpenAt(target?.Attribute, target?.Field == PackDiagnosticFields.GameName);
                break;

            case StudioProblemAction.AddAttributeValue when target is { Attribute: { } attribute, Value: { } value }:
                if (Edit(draft => DraftEdits.AddAttributeValue(draft, attribute, value)))
                {
                    _notifications.Add(
                        NotificationSeverity.Information,
                        DraftTitle,
                        Text.Format(nameof(Strings.Studio_Fix_ValueAdded), value, AttributeLabel(attribute)));
                }

                break;
        }

        return Task.CompletedTask;
    }

    private StudioCharacterRowViewModel? RowFor(string? internalName) =>
        internalName is null
            ? null
            : Rows.FirstOrDefault(r => string.Equals(r.InternalName, internalName, StringComparison.Ordinal))
              ?? Rows.FirstOrDefault(r => string.Equals(r.InternalName, internalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The characters and every other member of their families, for a problem about family links.</summary>
    private List<string> FamilyRows(IReadOnlyList<string> characters)
    {
        var draft = Session!.Current;
        var families = characters.Select(c => HashSharing.FamilyOf(draft, c) ?? c).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. characters,
            .. draft.Variants
                .Where(v => families.Contains(v.InternalName) || (v.BaseCharacterId is { } b && families.Contains(b)))
                .Select(v => v.InternalName),
        ];
    }

    private void SelectAll(IEnumerable<string> internalNames)
    {
        _selected.Clear();

        foreach (var name in internalNames)
        {
            _selected.Add(name);
        }

        ApplySelection();
    }

    private string AttributeLabel(string attribute) =>
        Session?.Current.Game.Attributes?.FirstOrDefault(a => string.Equals(a.Key, attribute, StringComparison.OrdinalIgnoreCase))
            .Value?.DisplayName is { Length: > 0 } label
            ? label
            : attribute;

    private string NameOf(string internalName) =>
        Session?.Current.Variants.FirstOrDefault(v => string.Equals(v.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
            is { DisplayName: { Length: > 0 } display }
            ? display
            : internalName;

    private string NamesOf(IReadOnlyList<string> internalNames)
    {
        var names = internalNames.Select(NameOf).ToList();

        return names.Count <= 1
            ? string.Join(string.Empty, names)
            : Text.Format(nameof(Strings.Studio_Names_And), string.Join(", ", names.Take(names.Count - 1)), names[^1]);
    }

    // Shared hashes, and who carries a hash

    private IReadOnlyList<SharedHash> _shared = [];

    private IReadOnlyList<string> _sharingHashes = [];

    /// <summary>Whether the shared-hashes panel is showing.</summary>
    [ObservableProperty]
    private bool _isSharingOpen;

    /// <summary>"Hashes Herta and The Herta share", or "Characters that carry 7eb5b84e".</summary>
    [ObservableProperty]
    private string _sharingTitle = string.Empty;

    /// <summary>What the list means and what the buttons do.</summary>
    [ObservableProperty]
    private string _sharingIntro = string.Empty;

    /// <summary>"Ignore these 28 hashes in the pack".</summary>
    [ObservableProperty]
    private string _sharingIgnoreText = string.Empty;

    /// <summary>Whether the "this would cost" question is showing over the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIgnoreCostOpen))]
    private HashIgnoreCostReport? _ignoreCost;

    /// <summary>Whether the question about what ignoring would cost is showing.</summary>
    public bool IsIgnoreCostOpen => IgnoreCost is not null;

    /// <summary>"Ignoring these 28 hashes would leave 2 characters unable to find their own mods."</summary>
    public string IgnoreCostTitle => IgnoreCost is not { } cost
        ? string.Empty
        : Text.Format(
            nameof(Strings.Studio_IgnoreCost_Title),
            Text.Hashes(cost.Adding),
            Text.Characters(cost.Losses.Count));

    /// <summary>The characters that would stop being found, and where their mods would go.</summary>
    public ObservableCollection<string> IgnoreCostLosses { get; } = [];

    /// <summary>The hashes, each with who carries it.</summary>
    public ObservableCollection<StudioSharedHashRowViewModel> SharingHashes { get; } = [];

    /// <summary>Every character that carries any of them, each with its buttons.</summary>
    public ObservableCollection<StudioSharingCharacterViewModel> SharingCharacters { get; } = [];

    /// <summary>Opens the panel on the hashes some families share.</summary>
    /// <param name="families">The families, by their heads' internal names.</param>
    public void OpenSharing(IReadOnlyList<string> families)
    {
        ArgumentNullException.ThrowIfNull(families);

        if (Session is not { } session)
        {
            return;
        }

        var shared = HashSharing.Between(session.Current, families);

        SharingTitle = Text.Format(nameof(Strings.Studio_Sharing_TitleFamilies), NamesOf(families));
        SharingIntro = Text[nameof(Strings.Studio_Sharing_IntroFamilies)];
        FillSharing(shared);
    }

    /// <summary>Opens the panel on every character that carries one hash.</summary>
    public void OpenCarriers(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (Session is not { } session)
        {
            return;
        }

        var carriers = HashSharing.Carriers(session.Current, hash);

        SharingTitle = Text.Format(nameof(Strings.Studio_Sharing_TitleHash), hash);
        SharingIntro = Text.Format(nameof(Strings.Studio_Sharing_IntroHash), hash, Text.Characters(carriers.Count));
        FillSharing(carriers.Count == 0 ? [] : [new SharedHash(hash, carriers)]);
    }

    private void FillSharing(IReadOnlyList<SharedHash> shared)
    {
        _shared = shared;
        _sharingHashes = [.. shared.Select(s => s.Hash)];

        SharingHashes.Clear();

        foreach (var hash in shared)
        {
            SharingHashes.Add(new StudioSharedHashRowViewModel(hash.Hash, NamesOf(hash.Characters)));
        }

        SharingCharacters.Clear();

        foreach (var carrier in shared
                     .SelectMany(s => s.Characters)
                     .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Count())
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            SharingCharacters.Add(new StudioSharingCharacterViewModel(
                carrier.Key,
                NameOf(carrier.Key),
                shared.Count == 1
                    ? string.Empty
                    : Text.Format(nameof(Strings.Studio_Sharing_Carries), carrier.Count(), shared.Count)));
        }

        SharingIgnoreText = shared.Count == 1
            ? Text[nameof(Strings.Studio_Sharing_IgnoreOne)]
            : Text.Format(nameof(Strings.Studio_Sharing_IgnoreMany), shared.Count);

        IsSharingOpen = true;
    }

    /// <summary>Keeps the hashes on one character's family only; every other character loses them.</summary>
    /// <param name="character">The character to keep them on.</param>
    [RelayCommand]
    public void KeepSharedOn(StudioSharingCharacterViewModel? character)
    {
        if (character is null || _sharingHashes.Count == 0)
        {
            return;
        }

        var hashes = _sharingHashes;

        if (Edit(draft => DraftEdits.KeepHashesOn(draft, character.InternalName, hashes).Draft))
        {
            IsSharingOpen = false;
            _notifications.Add(
                NotificationSeverity.Information,
                DraftTitle,
                Text.ForCount(hashes.Count, nameof(Strings.Studio_Sharing_Kept_One), nameof(Strings.Studio_Sharing_Kept), Text.Hashes(hashes.Count), character.DisplayName));
        }
    }

    /// <summary>Offers to ignore the shared hashes, measured first: that can lose a character its mods.</summary>
    [RelayCommand]
    public void IgnoreShared()
    {
        if (Session is not { } session || _sharingHashes.Count == 0)
        {
            return;
        }

        var cost = _ignoreCosts.Measure(session.Current, _sharingHashes);

        if (cost.IsFree)
        {
            ApplyIgnore();
            return;
        }

        IgnoreCostLosses.Clear();

        foreach (var loss in cost.Losses)
        {
            IgnoreCostLosses.Add(Text.Format(
                nameof(Strings.Studio_IgnoreCost_Loss),
                NameOf(loss.InternalName),
                loss.GoesToInstead is { } other && !loss.IsAmbiguous
                    ? NameOf(other)
                    : Text[nameof(Strings.Studio_IgnoreCost_Others)]));
        }

        IgnoreCost = cost;
        OnPropertyChanged(nameof(IgnoreCostTitle));
    }

    /// <summary>Ignores them anyway, having been told what it costs.</summary>
    [RelayCommand]
    public void IgnoreAnyway()
    {
        IgnoreCost = null;
        ApplyIgnore();
    }

    /// <summary>Closes the question and leaves the hashes alone.</summary>
    [RelayCommand]
    public void CancelIgnore() => IgnoreCost = null;

    private void ApplyIgnore()
    {
        var hashes = _sharingHashes;

        if (hashes.Count > 0 && Edit(draft => DraftEdits.IgnoreHashes(draft, hashes).Draft))
        {
            IsSharingOpen = false;
            _notifications.Add(
                NotificationSeverity.Information,
                DraftTitle,
                Text.Format(nameof(Strings.Studio_Sharing_Ignored), Text.Hashes(hashes.Count)));
        }
    }

    /// <summary>Opens one character's hashes, with the shared ones marked.</summary>
    [RelayCommand]
    public void OpenSharedHashesOf(StudioSharingCharacterViewModel? character)
    {
        if (character is null || RowFor(character.InternalName) is not { } row)
        {
            return;
        }

        var reasons = _shared.ToDictionary(
            h => h.Hash,
            h => NamesOf([.. h.Characters.Where(c => !string.Equals(c, character.InternalName, StringComparison.OrdinalIgnoreCase))]),
            StringComparer.OrdinalIgnoreCase);

        IsSharingOpen = false;
        SelectAll([row.InternalName]);
        OpenHashes(
            row,
            [
                .. row.Hashes
                    .Where(reasons.ContainsKey)
                    .Select(h => new StudioMarkedHashViewModel(h, Text.Format(nameof(Strings.Studio_Hashes_Mark_Shared), reasons[h]))),
            ]);
    }

    /// <summary>Closes the shared-hashes panel.</summary>
    [RelayCommand]
    public void CloseSharing() => IsSharingOpen = false;

    // A picture for a character

    /// <summary>Copies a picture into the draft under a name of its own and makes it the character's.</summary>
    /// <returns>True when the draft has it.</returns>
    private async Task<bool> StorePictureAsync(StudioCharacterRowViewModel row, PreviewImageSource image, CancellationToken ct)
    {
        if (Session is not { } session)
        {
            return false;
        }

        var path = await _store.StoreImageAsync(session.Current.GameId, row.InternalName, image, ct).ConfigureAwait(true);

        // The draft changed underneath the copy: nowhere to write it.
        return ReferenceEquals(Session, session)
               && Edit(draft => DraftEdits.EditCharacter(
                   draft, row.InternalName, new CharacterEdit { Image = EditField<string>.To(path) }).Draft);
    }

    /// <summary>Copies a portrait that is a file on this computer into the draft, so the pack carries it.</summary>
    private Task<bool> CopyPictureInAsync(StudioCharacterRowViewModel row) =>
        _runner.RunAsync(
            Heading,
            async ct =>
            {
                var image = Session?.Current.Variants
                    .FirstOrDefault(v => string.Equals(v.InternalName, row.InternalName, StringComparison.Ordinal))?.Image;

                if (image is not { Length: > 0 })
                {
                    return;
                }

                var file = Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : image;

                await StorePictureAsync(row, PreviewImageSource.FromFile(file), ct).ConfigureAwait(true);
            },
            ActivationToken);

    // Which outfit is the default

    /// <summary>Whether the "which is the default?" question is showing.</summary>
    [ObservableProperty]
    private bool _isDefaultChoiceOpen;

    /// <summary>"Which outfit is Herta's default?".</summary>
    [ObservableProperty]
    private string _defaultChoiceTitle = string.Empty;

    /// <summary>The family's members, its head first.</summary>
    public ObservableCollection<StudioDefaultChoiceViewModel> DefaultChoices { get; } = [];

    private void OpenDefaultChoice(IReadOnlyList<string> family)
    {
        if (Session is not { } session || family.Count == 0)
        {
            return;
        }

        var head = family[0];

        DefaultChoices.Clear();

        foreach (var id in family)
        {
            var variant = session.Current.Variants.FirstOrDefault(v => string.Equals(v.InternalName, id, StringComparison.OrdinalIgnoreCase));

            // As the merge does: a base with no flag is its family's default, an outfit is not.
            var isDefault = variant?.IsDefaultVariant ?? string.Equals(id, head, StringComparison.OrdinalIgnoreCase);

            DefaultChoices.Add(new StudioDefaultChoiceViewModel(id, NameOf(id), isDefault));
        }

        DefaultChoiceTitle = Text.Format(nameof(Strings.Studio_Default_Title), NameOf(head));
        IsDefaultChoiceOpen = true;
    }

    /// <summary>Makes one outfit its family's default, and no other.</summary>
    [RelayCommand]
    public void MakeDefault(StudioDefaultChoiceViewModel? choice)
    {
        if (choice is not null && Edit(draft => DraftEdits.SetDefault(draft, choice.InternalName)))
        {
            IsDefaultChoiceOpen = false;
        }
    }

    /// <summary>Closes the question without changing anything.</summary>
    [RelayCommand]
    public void CloseDefaultChoice() => IsDefaultChoiceOpen = false;

    // Hashes no character claims

    /// <summary>The name the hashes are filed under, or null when the question is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnclaimedOpen), nameof(UnclaimedText), nameof(UnclaimedCreateText))]
    private string? _unclaimedName;

    /// <summary>Whether the question is showing.</summary>
    public bool IsUnclaimedOpen => UnclaimedName is not null;

    /// <summary>"12 hashes filed under 'Ghost', which no character has. …".</summary>
    public string UnclaimedText => UnclaimedName is { } name && Session is { } session
        ? Text.Format(
            nameof(Strings.Studio_Unclaimed_Text),
            Text.Hashes((session.Current.Hashes.Entries ?? []).Count(e => string.Equals(e.Variant, name, StringComparison.OrdinalIgnoreCase))),
            name)
        : string.Empty;

    /// <summary>"Create 'Ghost'".</summary>
    public string UnclaimedCreateText => UnclaimedName is { } name
        ? Text.Format(nameof(Strings.Studio_Unclaimed_Create), name)
        : string.Empty;

    private void OpenUnclaimed(string name) => UnclaimedName = name;

    /// <summary>Creates a character with the name the hashes are filed under, so they are its.</summary>
    [RelayCommand]
    public void ClaimUnclaimed()
    {
        if (UnclaimedName is not { } name)
        {
            return;
        }

        if (Edit(draft => DraftEdits.AddCharacter(draft, new NewCharacter { DisplayName = name, InternalName = name }).Draft))
        {
            UnclaimedName = null;
            Select(name);
        }
    }

    /// <summary>Deletes the hashes. One Undo puts them back.</summary>
    [RelayCommand]
    public void DeleteUnclaimed()
    {
        if (UnclaimedName is { } name && Edit(draft => DraftEdits.RemoveUnclaimedHashes(draft, name).Draft))
        {
            UnclaimedName = null;
        }
    }

    /// <summary>Closes the question without changing anything.</summary>
    [RelayCommand]
    public void CloseUnclaimed() => UnclaimedName = null;
}
