using System.Globalization;
using System.Text.Json;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Overlays;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Characters;

/// <summary>The default <see cref="ICharacterEditor"/>: every Character Manager write to the overlay.</summary>
public sealed class CharacterEditor(
    IOverlayStore overlays,
    IModRepository mods,
    IModFileOperations modFiles,
    IAppPaths paths,
    TimeProvider time,
    ILogger logger) : ICharacterEditor
{
    private const string DeletionsDirectoryName = "deleted-characters";

    private readonly IOverlayStore _overlays = overlays;
    private readonly IModRepository _mods = mods;
    private readonly IModFileOperations _modFiles = modFiles;
    private readonly IAppPaths _paths = paths;
    private readonly TimeProvider _time = time;
    private readonly ILogger _logger = logger.ForContext<CharacterEditor>();

    /// <inheritdoc />
    public async Task<CharacterEditResult> CreateAsync(
        GameData data, NewCharacter request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ModOperationException(
                "A character needs a name. Nothing else is required — everything is derived from it.",
                data.GameId);
        }

        var displayName = request.DisplayName.Trim();

        // Everything that reads the overlay is inside the change: it may run again.
        var created = await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var diagnostics = new List<Diagnostic>();

                var taken = data.Variants
                    .Select(variant => variant.InternalName)
                    .Concat((overlay.Variants ?? new Dictionary<string, OverlayVariant>()).Keys);

                var proposal = CharacterNames.ProposeAmong(
                    string.IsNullOrWhiteSpace(request.InternalName) ? displayName : request.InternalName,
                    taken);

                if (!proposal.IsFree)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Info,
                        CharacterDiagnosticCodes.InternalNameAdjusted,
                        $"'{proposal.Wanted}' is already used by '{proposal.CollidesWith}', so the new " +
                        $"character is stored as '{proposal.Name}'. Its name is still '{displayName}'."));
                }

                var internalName = proposal.Name;

                if (request.BaseCharacterId is { Length: > 0 } baseId && data.Find(baseId) is null)
                {
                    diagnostics.Add(UnknownBase(baseId));
                }

                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OverlayFields.DisplayName };
                var entry = new OverlayVariant
                {
                    Origin = VariantOrigin.Custom,
                    DisplayName = displayName,
                    CreatedAt = _time.GetUtcNow(),
                };

                entry = Set(entry, fields, OverlayFields.BaseCharacterId, request.BaseCharacterId);
                entry = Set(entry, fields, OverlayFields.ModFilesName, request.ModFilesName);
                entry = Set(entry, fields, OverlayFields.Image, request.Image);
                entry = Set(entry, fields, OverlayFields.Notes, request.Notes);

                if (request.IsDefaultVariant is { } isDefault)
                {
                    entry = entry with { IsDefaultVariant = isDefault };
                    fields.Add(OverlayFields.IsDefaultVariant);
                }

                if (request.Aliases is { Count: > 0 } aliases)
                {
                    entry = entry with { Aliases = [.. aliases] };
                    fields.Add(OverlayFields.Aliases);
                }

                if (request.Attributes is { Count: > 0 } attributes)
                {
                    entry = entry with { Attributes = attributes };
                    fields.Add(OverlayFields.Attributes);
                }

                if (request.Hidden)
                {
                    entry = entry with { Hidden = true };
                    fields.Add(OverlayFields.Hidden);
                }

                entry = entry with { SpecifiedFields = fields };

                var variants = Variants(overlay);
                variants[internalName] = entry;

                var hashes = Normalise(request.Hashes, internalName);
                var updated = overlay with { GameId = data.GameId, Variants = variants };

                if (hashes.Count > 0)
                {
                    diagnostics.AddRange(Inspect(data, internalName, hashes, existing: []));
                    updated = WithAddedHashes(updated, hashes);
                }

                return OverlayUpdate.Write(
                    updated, (Name: internalName, Proposal: proposal, Diagnostics: diagnostics, Hashes: hashes.Count));
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Created character {InternalName} ({DisplayName}) for {GameId} with {Hashes} hashes",
            created.Name,
            displayName,
            data.GameId,
            created.Hashes);

        return new CharacterEditResult
        {
            InternalName = created.Name,
            DisplayName = displayName,
            Origin = VariantOrigin.Custom,
            Changed = true,
            RequestedInternalName = created.Proposal.IsFree ? null : created.Proposal.Wanted,
            Diagnostics = created.Diagnostics,
            HashCount = created.Hashes,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterEditResult> EditAsync(
        GameData data,
        string internalName,
        CharacterEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(edit);

        var variant = Require(data, internalName);

        if (edit.IsEmpty)
        {
            return Unchanged(variant);
        }

        if (edit.DisplayName is { IsSet: true, Value: var newName } && string.IsNullOrWhiteSpace(newName))
        {
            throw new ModOperationException(
                $"'{variant.DisplayName}' cannot be given a blank name. To go back to the pack's " +
                "name, reset the name instead.",
                internalName);
        }

        if (edit.BaseCharacterId is { IsSet: true, Value: { Length: > 0 } ownId }
            && PathComparer.AreNamesEqual(ownId, variant.InternalName))
        {
            throw new ModOperationException(
                $"'{variant.DisplayName}' cannot be an outfit of itself.",
                internalName);
        }

        var diagnostics = new List<Diagnostic>();

        if (edit.BaseCharacterId is { IsSet: true, Value: { Length: > 0 } baseId } && data.Find(baseId) is null)
        {
            diagnostics.Add(UnknownBase(baseId));
        }

        var edited = await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var variants = Variants(overlay);
                var entry = Existing(variants, internalName);
                var fields = Specified(entry);

                entry = Apply(entry, fields, OverlayFields.DisplayName, edit.DisplayName, value => value.Trim());
                entry = Apply(entry, fields, OverlayFields.BaseCharacterId, edit.BaseCharacterId);
                entry = Apply(entry, fields, OverlayFields.ModFilesName, edit.ModFilesName);
                entry = Apply(entry, fields, OverlayFields.Image, edit.Image);
                entry = Apply(entry, fields, OverlayFields.Notes, edit.Notes);

                if (edit.IsDefaultVariant.IsSet)
                {
                    entry = entry with { IsDefaultVariant = edit.IsDefaultVariant.Value };
                    fields.Add(OverlayFields.IsDefaultVariant);
                }

                if (edit.Hidden.IsSet)
                {
                    // Asking for the pack's own hidden value takes the override out rather than locking the character.
                    if (variant.Origin != VariantOrigin.Custom && edit.Hidden.Value == PackHidden(data, internalName))
                    {
                        fields.Remove(OverlayFields.Hidden);
                        entry = entry with { Hidden = null };
                    }
                    else
                    {
                        entry = entry with { Hidden = edit.Hidden.Value };
                        fields.Add(OverlayFields.Hidden);
                    }
                }

                if (edit.Aliases.IsSet)
                {
                    entry = entry with
                    {
                        Aliases = edit.Aliases.Value is { } list
                            ? [.. list.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim())]
                            : null,
                    };

                    fields.Add(OverlayFields.Aliases);
                }

                if (edit.Attributes.IsSet)
                {
                    entry = entry with { Attributes = edit.Attributes.Value };
                    fields.Add(OverlayFields.Attributes);
                }

                if (fields.Count == 0 && variant.Origin != VariantOrigin.Custom && !HasOwnHashes(overlay, internalName))
                {
                    return variants.Remove(internalName)
                        ? OverlayUpdate.Write(overlay with { GameId = data.GameId, Variants = variants }, (Origin: (VariantOrigin?)VariantOrigin.Pack, Fields: fields))
                        : OverlayUpdate.Keep((Origin: (VariantOrigin?)null, Fields: fields));
                }

                // Touching any field of a pack variant makes it modified and locks it, on purpose.
                entry = entry with
                {
                    SpecifiedFields = fields,
                    Origin = entry.Origin ?? VariantOrigin.Modified,
                    Locked = entry.Locked ?? true,
                };

                variants[internalName] = entry;

                return OverlayUpdate.Write(
                    overlay with { GameId = data.GameId, Variants = variants },
                    (Origin: (VariantOrigin?)(entry.Origin ?? VariantOrigin.Modified), Fields: fields));
            },
            cancellationToken).ConfigureAwait(false);

        if (edited.Origin is not { } origin)
        {
            return Unchanged(variant);
        }

        if (origin == VariantOrigin.Pack)
        {
            _logger.Information(
                "Edited character {InternalName} for {GameId} back to the pack's own values. Origin is now {Origin}",
                internalName,
                data.GameId,
                VariantOrigin.Pack);

            return new CharacterEditResult
            {
                InternalName = internalName,
                DisplayName = variant.DisplayName,
                Origin = VariantOrigin.Pack,
                Changed = true,
                Diagnostics = diagnostics,
                HashCount = variant.Hashes.Count,
            };
        }

        _logger.Information(
            "Edited character {InternalName} for {GameId}: {Fields}. Origin is now {Origin}",
            internalName,
            data.GameId,
            string.Join(", ", edited.Fields),
            origin);

        return new CharacterEditResult
        {
            InternalName = internalName,
            DisplayName = edit.DisplayName is { IsSet: true, Value: { Length: > 0 } typed }
                ? typed.Trim()
                : variant.DisplayName,
            Origin = origin,
            Changed = true,
            Diagnostics = diagnostics,
            HashCount = variant.Hashes.Count,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterEditResult> AddHashesAsync(
        GameData data,
        string internalName,
        IReadOnlyList<PackHashEntry> hashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(hashes);

        var variant = Require(data, internalName);

        var incoming = Normalise(hashes, variant.InternalName);
        var diagnostics = Inspect(data, variant.InternalName, incoming, variant.Hashes);

        var fresh = incoming
            .Where(entry => !variant.Hashes.Any(existing => Same(existing, entry)))
            .ToList();

        if (fresh.Count == 0)
        {
            return Unchanged(variant) with { Diagnostics = diagnostics };
        }

        await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var updated = WithAddedHashes(overlay with { GameId = data.GameId }, fresh);

                return OverlayUpdate.Write(MarkEdited(updated, variant.InternalName), true);
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Added {Count} hashes to {InternalName} for {GameId}",
            fresh.Count,
            variant.InternalName,
            data.GameId);

        return new CharacterEditResult
        {
            InternalName = variant.InternalName,
            DisplayName = variant.DisplayName,
            Origin = variant.Origin == VariantOrigin.Pack ? VariantOrigin.Modified : variant.Origin,
            Changed = true,
            Diagnostics = diagnostics,
            HashCount = variant.Hashes.Count + fresh.Count,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterEditResult> RemoveHashesAsync(
        GameData data,
        string internalName,
        IReadOnlyList<string> hashes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(hashes);

        var variant = Require(data, internalName);

        var wanted = hashes
            .Select(Core.Hashes.HashText.Normalize)
            .Where(hash => hash is not null)
            .Select(hash => hash!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var diagnostics = new List<Diagnostic>();
        var present = new List<PackHashEntry>();

        foreach (var hash in wanted)
        {
            var matches = variant.Hashes.Where(entry =>
                string.Equals(entry.Hash, hash, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Info,
                    CharacterDiagnosticCodes.HashNotPresent,
                    $"{variant.DisplayName} does not have the hash {hash}, so nothing was removed for it."));

                continue;
            }

            present.AddRange(matches);
        }

        if (present.Count == 0)
        {
            return Unchanged(variant) with { Diagnostics = diagnostics };
        }

        await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var existing = overlay.Hashes ?? new OverlayHashes();

                // Both lists kept: out of add undoes the user's paste; into remove hides the pack's hash.
                var add = (existing.Add ?? [])
                    .Where(entry => !present.Any(target => Same(entry, target)))
                    .ToList();

                var remove = new List<PackHashEntry>(existing.Remove ?? []);

                foreach (var entry in present)
                {
                    var wasOurs = (existing.Add ?? []).Any(candidate => Same(candidate, entry));

                    if (!wasOurs && !remove.Any(candidate => Same(candidate, entry)))
                    {
                        remove.Add(entry);
                    }
                }

                var updated = overlay with
                {
                    GameId = data.GameId,
                    Hashes = existing with { Add = add, Remove = remove },
                };

                return OverlayUpdate.Write(MarkEdited(updated, variant.InternalName), true);
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Removed {Count} hashes from {InternalName} for {GameId}",
            present.Count,
            variant.InternalName,
            data.GameId);

        return new CharacterEditResult
        {
            InternalName = variant.InternalName,
            DisplayName = variant.DisplayName,
            Origin = variant.Origin == VariantOrigin.Pack ? VariantOrigin.Modified : variant.Origin,
            Changed = true,
            Diagnostics = diagnostics,
            HashCount = variant.Hashes.Count - present.Count,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterEditResult> ResetAsync(
        GameData data,
        string internalName,
        IReadOnlyList<string>? fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var variant = Require(data, internalName);

        if (fields is null or { Count: 0 } && variant.Origin == VariantOrigin.Custom)
        {
            throw new ModOperationException(
                $"'{variant.DisplayName}' was created by you, so there is no pack version to reset " +
                "to. Delete it instead — which asks where its mods should go first.",
                internalName);
        }

        // Checked before the overlay is read: the change may be repeated and must not throw.
        var named = new List<string>();

        foreach (var field in fields ?? [])
        {
            if (PathComparer.AreNamesEqual(field, OverlayFields.Hashes))
            {
                named.Add(OverlayFields.Hashes);
                continue;
            }

            named.Add(OverlayFields.All.FirstOrDefault(name => PathComparer.AreNamesEqual(name, field))
                      ?? throw new ModOperationException(
                          $"'{field}' is not a field a character has. " +
                          $"The fields are: {string.Join(", ", OverlayFields.All)}.",
                          internalName));
        }

        var outcome = await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var variants = Variants(overlay);

                if (!variants.TryGetValue(internalName, out var entry))
                {
                    return OverlayUpdate.Keep((Reset: (List<string>?)null, StillOverrides: true));
                }

                if (named.Count == 0)
                {
                    variants.Remove(internalName);

                    return OverlayUpdate.Write(
                        ClearHashes(overlay with { GameId = data.GameId, Variants = variants }, internalName),
                        (Reset: (List<string>?)[], StillOverrides: false));
                }

                var specified = Specified(entry);
                var reset = new List<string>();
                var updatedOverlay = overlay with { GameId = data.GameId };

                foreach (var canonical in named)
                {
                    if (canonical == OverlayFields.Hashes)
                    {
                        updatedOverlay = ClearHashes(updatedOverlay, internalName);
                        reset.Add(OverlayFields.Hashes);
                    }
                    else if (specified.Remove(canonical))
                    {
                        entry = Clear(entry, canonical);
                        reset.Add(canonical);
                    }
                }

                if (reset.Count == 0)
                {
                    return OverlayUpdate.Keep((Reset: (List<string>?)null, StillOverrides: true));
                }

                // An entry that no longer overrides anything is removed, so origin goes back to pack.
                var stillOverrides = specified.Count > 0
                    || HasOwnHashes(updatedOverlay, internalName)
                    || variant.Origin == VariantOrigin.Custom;

                if (stillOverrides)
                {
                    variants[internalName] = entry with { SpecifiedFields = specified };
                }
                else
                {
                    variants.Remove(internalName);
                }

                return OverlayUpdate.Write(
                    updatedOverlay with { Variants = variants },
                    (Reset: (List<string>?)reset, StillOverrides: stillOverrides));
            },
            cancellationToken).ConfigureAwait(false);

        if (outcome.Reset is not { } resetFields)
        {
            return Unchanged(variant);
        }

        if (resetFields.Count == 0)
        {
            _logger.Information(
                "Reset {InternalName} for {GameId} to the pack's version", internalName, data.GameId);

            return new CharacterEditResult
            {
                InternalName = internalName,
                DisplayName = data.Pack?.Variants
                    .FirstOrDefault(v => PathComparer.AreNamesEqual(v.InternalName, internalName))
                    ?.DisplayName ?? internalName,
                Origin = VariantOrigin.Pack,
                Changed = true,
            };
        }

        _logger.Information(
            "Reset {Fields} on {InternalName} for {GameId}",
            string.Join(", ", resetFields),
            internalName,
            data.GameId);

        return new CharacterEditResult
        {
            InternalName = internalName,
            DisplayName = variant.DisplayName,
            Origin = outcome.StillOverrides ? variant.Origin : VariantOrigin.Pack,
            Changed = true,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterEditResult> UnlockAsync(
        GameData data,
        string internalName,
        IReadOnlyList<string>? fields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        var variant = Require(data, internalName);

        if (variant.Origin == VariantOrigin.Custom)
        {
            throw new ModOperationException(
                $"'{variant.DisplayName}' was created by you, so there is nothing upstream for a " +
                "pack update to bring in. Unlocking it would change nothing.",
                internalName);
        }

        var keep = (fields ?? [])
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => OverlayFields.All.FirstOrDefault(name => PathComparer.AreNamesEqual(name, field))
                             ?? throw new ModOperationException(
                                 $"'{field}' is not a field a character has. " +
                                 $"The fields are: {string.Join(", ", OverlayFields.All)}.",
                                 internalName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unlocked = await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var variants = Variants(overlay);

                if (!variants.TryGetValue(internalName, out var entry))
                {
                    return OverlayUpdate.Keep(false);
                }

                variants[internalName] = entry with
                {
                    Locked = false,
                    LockedFields = keep.Count == 0 ? null : keep,
                    SpecifiedFields = Specified(entry),
                };

                return OverlayUpdate.Write(overlay with { GameId = data.GameId, Variants = variants }, true);
            },
            cancellationToken).ConfigureAwait(false);

        if (!unlocked)
        {
            return Unchanged(variant);
        }

        _logger.Information(
            "Unlocked {InternalName} for {GameId}; still protected: {Fields}",
            internalName,
            data.GameId,
            keep.Count == 0 ? "nothing" : string.Join(", ", keep));

        return new CharacterEditResult
        {
            InternalName = internalName,
            DisplayName = variant.DisplayName,
            Origin = variant.Origin,
            Changed = true,
            HashCount = variant.Hashes.Count,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterDeleteResult> DeleteAsync(
        GameData data,
        string internalName,
        string modsDirectory,
        string? rehomeTo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);

        var variant = Require(data, internalName);

        if (variant.Origin != VariantOrigin.Custom)
        {
            throw new ModOperationException(
                $"'{variant.DisplayName}' came from the pack, so it cannot be deleted. " +
                "Hide it instead, which removes it from the grid and deletes nothing.",
                internalName);
        }

        var destinationName = ModsFolderLayout.UnsortedFolderName;

        if (rehomeTo is { Length: > 0 })
        {
            var target = data.Find(rehomeTo)
                ?? throw new ModOperationException(
                    $"There is no character called '{rehomeTo}' to move " +
                    $"{variant.DisplayName}'s mods to.",
                    rehomeTo);

            if (PathComparer.AreNamesEqual(target.InternalName, variant.InternalName))
            {
                throw new ModOperationException(
                    $"'{variant.DisplayName}'s mods cannot be moved to itself.", internalName);
            }

            destinationName = target.ModFilesName;
        }

        var inventory = await _mods.ScanAsync(modsDirectory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var folder = inventory.VariantFolders
            .FirstOrDefault(candidate => PathComparer.AreNamesEqual(candidate.Name, variant.ModFilesName));

        var diagnostics = new List<Diagnostic>();
        var moved = new List<string>();
        var moves = new List<RehomedMod>();

        if (folder is { Mods.Count: > 0 })
        {
            var destination = PathComparer.Join(modsDirectory, destinationName);

            foreach (var mod in folder.Mods)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await _modFiles.MoveAsync(mod.Path, destination, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                moved.Add(mod.Name);
                moves.Add(new RehomedMod(mod.Path, result.ToPath));

                if (result.DisambiguatedFromName is { Length: > 0 } original)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Info,
                        CharacterDiagnosticCodes.ModRehomed,
                        $"'{PathDisplay.Show(original)}' was already taken under {destinationName}, so the mod moved " +
                        $"there as '{result.ToName}'."));
                }
            }
        }

        var (deletion, removed) = await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var variants = Variants(overlay);
                var taken = variants.TryGetValue(internalName, out var entry)
                    ? DeletionOf(data, variant, entry, overlay, moves)
                    : null;
                var gone = variants.Remove(internalName);

                var updated = ClearHashes(overlay with { GameId = data.GameId, Variants = variants }, internalName);

                if (updated.DeclinedAdoptions is { Count: > 0 } declined
                    && declined.Keys.Any(key => PathComparer.AreNamesEqual(key, internalName)))
                {
                    updated = updated with
                    {
                        DeclinedAdoptions = declined
                            .Where(pair => !PathComparer.AreNamesEqual(pair.Key, internalName))
                            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                    };
                }

                return OverlayUpdate.Write(updated, (taken, gone));
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Deleted custom character {InternalName} from {GameId}; {Count} mods re-homed to {Destination}",
            internalName,
            data.GameId,
            moved.Count,
            destinationName);

        string? recordPath = null;

        if (deletion is not null)
        {
            // Not cancellable: the character is already gone, and the record is the only way back.
            recordPath = await WriteDeletionAsync(deletion, path: null, CancellationToken.None).ConfigureAwait(false);

            if (recordPath is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    CharacterDiagnosticCodes.RestoreRecordNotWritten,
                    $"'{variant.DisplayName}' was deleted, but the record for undoing it later could not be " +
                    "saved. It can still be undone from this window until it is closed."));
            }
        }

        return new CharacterDeleteResult
        {
            InternalName = internalName,
            Changed = removed,
            RehomedTo = moved.Count == 0 ? null : destinationName,
            RehomedMods = moved,
            Diagnostics = diagnostics,
            Deletion = deletion,
            RestoreRecordPath = recordPath,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterRestoreResult> RestoreDeletedAsync(
        GameData data,
        CharacterDeletion deletion,
        string? recordPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(deletion);

        if (!string.Equals(deletion.GameId, data.GameId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModOperationException(
                $"'{deletion.DisplayName}' was deleted from {deletion.GameId}, not {data.GameId}. " +
                "Switch to that game to bring it back.",
                deletion.InternalName);
        }

        if (!deletion.CharacterRestored)
        {
            await RestoreEntryAsync(data, deletion, cancellationToken).ConfigureAwait(false);
        }

        var restored = new List<string>();
        var skipped = new List<CharacterRestoreSkip>();
        var remaining = new List<RehomedMod>();
        var diagnostics = new List<Diagnostic>();

        foreach (var move in deletion.Moves)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!PathComparer.TryResolveExisting(move.MovedTo, out var current) || !Directory.Exists(current))
            {
                skipped.Add(new CharacterRestoreSkip(
                    move.MovedTo,
                    $"'{PathDisplay.Show(move.MovedTo)}' is not there any more — it was moved, renamed or deleted since — " +
                    "so it could not be moved back."));
                remaining.Add(move);
                continue;
            }

            try
            {
                var result = await _modFiles
                    .MoveAsync(
                        current,
                        Path.GetDirectoryName(move.OriginalPath)!,
                        Path.GetFileName(move.OriginalPath),
                        cancellationToken)
                    .ConfigureAwait(false);

                restored.Add(result.ToPath);

                if (result.DisambiguatedFromName is { Length: > 0 } wanted)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticSeverity.Info,
                        CharacterDiagnosticCodes.ModRehomed,
                        $"'{PathDisplay.Show(wanted)}' was already taken, so the mod came back as '{Path.GetFileName(result.ToPath)}'."));
                }
            }
            catch (ModOperationException exception)
            {
                skipped.Add(new CharacterRestoreSkip(move.MovedTo, exception.Message));
                remaining.Add(move);
            }
        }

        var left = deletion with { CharacterRestored = true, Moves = remaining };

        if (recordPath is { Length: > 0 })
        {
            if (remaining.Count == 0)
            {
                TryDeleteRecord(recordPath);
            }
            else
            {
                await WriteDeletionAsync(left, recordPath, CancellationToken.None).ConfigureAwait(false);
            }
        }

        _logger.Information(
            "Restored deleted character {InternalName} for {GameId}: {Restored} mods moved back, {Skipped} could not be",
            deletion.InternalName,
            data.GameId,
            restored.Count,
            skipped.Count);

        return new CharacterRestoreResult
        {
            InternalName = deletion.InternalName,
            DisplayName = deletion.DisplayName,
            RestoredMods = restored,
            Skipped = skipped,
            Diagnostics = diagnostics,
            Remaining = left,
        };
    }

    /// <inheritdoc />
    public async Task<CharacterDeletion> ReadDeletionAsync(string recordPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);

        if (!PathComparer.TryResolveExisting(recordPath, out var resolved) || !File.Exists(resolved))
        {
            throw new ModOperationException(
                $"There is no deleted-character record at '{PathDisplay.Show(recordPath)}'. It may already have been restored.",
                recordPath);
        }

        try
        {
            await using var stream = File.OpenRead(resolved);

            return await JsonSerializer
                       .DeserializeAsync(stream, PackJsonContext.Default.CharacterDeletion, cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw new ModOperationException($"'{PathDisplay.Show(resolved)}' is empty, so there is nothing to restore.", resolved);
        }
        catch (JsonException ex)
        {
            throw new ModOperationException(
                $"'{PathDisplay.Show(resolved)}' is not a deleted-character record XXSM can read: {ex.Message}", resolved, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException($"Could not read '{PathDisplay.Show(resolved)}': {ex.Message}", resolved, ex);
        }
    }

    /// <summary>Puts a deleted character's overlay entry and hashes back. Refuses to overwrite.</summary>
    private async Task RestoreEntryAsync(GameData data, CharacterDeletion deletion, CancellationToken cancellationToken)
    {
        await _overlays.UpdateAsync(
            data.GameId,
            overlay =>
            {
                var variants = Variants(overlay);

                if (data.Find(deletion.InternalName) is { } taken || variants.ContainsKey(deletion.InternalName))
                {
                    var holder = data.Find(deletion.InternalName)?.DisplayName ?? deletion.InternalName;

                    throw new ModOperationException(
                        $"'{holder}' now uses the id '{deletion.InternalName}', so '{deletion.DisplayName}' cannot come " +
                        "back under it. Nothing was changed.",
                        deletion.InternalName);
                }

                variants[deletion.InternalName] = deletion.Entry;

                var updated = overlay with { GameId = data.GameId, Variants = variants };

                if (deletion.AddedHashes.Count > 0 || deletion.RemovedHashes.Count > 0 || deletion.HashMode is not null)
                {
                    var hashes = overlay.Hashes ?? new OverlayHashes();
                    var add = new List<PackHashEntry>(hashes.Add ?? []);
                    var remove = new List<PackHashEntry>(hashes.Remove ?? []);
                    add.AddRange(deletion.AddedHashes);
                    remove.AddRange(deletion.RemovedHashes);

                    var modes = new Dictionary<string, HashMergeMode>(
                        hashes.VariantModes ?? new Dictionary<string, HashMergeMode>(), StringComparer.OrdinalIgnoreCase);

                    if (deletion.HashMode is { } mode)
                    {
                        modes[deletion.InternalName] = mode;
                    }

                    updated = updated with
                    {
                        Hashes = hashes with
                        {
                            Add = add.Count > 0 ? add : null,
                            Remove = remove.Count > 0 ? remove : null,
                            VariantModes = modes.Count > 0 ? modes : null,
                        },
                    };
                }

                if (deletion.DeclinedAdoption is { Count: > 0 } declined)
                {
                    var adoptions = new Dictionary<string, IReadOnlyList<string>>(
                        overlay.DeclinedAdoptions ?? new Dictionary<string, IReadOnlyList<string>>(),
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [deletion.InternalName] = declined,
                    };

                    updated = updated with { DeclinedAdoptions = adoptions };
                }

                return OverlayUpdate.Write(updated, true);
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Restored the overlay entry of deleted character {InternalName} ({DisplayName}) for {GameId}",
            deletion.InternalName,
            deletion.DisplayName,
            data.GameId);
    }

    private CharacterDeletion DeletionOf(
        GameData data, MergedVariant variant, OverlayVariant entry, PackOverlay overlay, List<RehomedMod> moves)
    {
        var name = variant.InternalName;
        var hashes = overlay.Hashes;

        return new CharacterDeletion
        {
            GameId = data.GameId,
            InternalName = name,
            DisplayName = variant.DisplayName,
            DeletedAt = _time.GetUtcNow(),
            Entry = entry,
            AddedHashes = [.. (hashes?.Add ?? []).Where(hash => PathComparer.AreNamesEqual(hash.Variant, name))],
            RemovedHashes = [.. (hashes?.Remove ?? []).Where(hash => PathComparer.AreNamesEqual(hash.Variant, name))],
            HashMode = hashes?.VariantModes?
                .Where(pair => PathComparer.AreNamesEqual(pair.Key, name))
                .Select(pair => (HashMergeMode?)pair.Value)
                .FirstOrDefault(),
            DeclinedAdoption = overlay.DeclinedAdoptions?
                .Where(pair => PathComparer.AreNamesEqual(pair.Key, name))
                .Select(pair => pair.Value)
                .FirstOrDefault(),
            Moves = moves,
        };
    }

    /// <summary>Writes a deletion's record, new or over its old file; null when it could not (logged).</summary>
    private async Task<string?> WriteDeletionAsync(CharacterDeletion deletion, string? path, CancellationToken cancellationToken)
    {
        var destination = path ?? Path.Combine(
            _paths.StateDirectory,
            DeletionsDirectoryName,
            SafeFileName(deletion.GameId),
            SafeFileName(deletion.InternalName) + "-" +
            deletion.DeletedAt.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + ".json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await AtomicFile.WriteAsync(
                    destination,
                    (stream, token) => JsonSerializer.SerializeAsync(stream, deletion, PackJsonContext.Default.CharacterDeletion, token),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not write the restore record for {InternalName} at {Path}", deletion.InternalName, destination);
            return null;
        }

        _logger.Information(
            "Recorded deleted character {InternalName} for undo at {Path}", deletion.InternalName, destination);

        return destination;
    }

    private void TryDeleteRecord(string path)
    {
        // Only a record in XXSM's own folder: the path can come from the command line.
        if (!UntrustedLocation.IsSameOrUnderExactly(Path.Combine(_paths.StateDirectory, DeletionsDirectoryName), path))
        {
            _logger.Warning("Not removing {Path}: it is not in XXSM's own record folder", path);
            return;
        }

        try
        {
            // File.Delete on purpose: XXSM's own bookkeeping, not user content.
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Restored everything but could not remove the spent record {Path}", path);
        }
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static MergedVariant Require(GameData data, string internalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internalName);

        return data.Find(internalName)
            ?? throw new ModOperationException(
                $"There is no character called '{internalName}' in {data.GameId}.", internalName);
    }

    /// <summary>Whether the installed pack itself hides a variant. False when it has no such variant.</summary>
    private static bool PackHidden(GameData data, string internalName) =>
        data.Pack?.Variants
            .FirstOrDefault(variant => PathComparer.AreNamesEqual(variant.InternalName, internalName))
            ?.Hidden ?? false;

    private static CharacterEditResult Unchanged(MergedVariant variant) => new()
    {
        InternalName = variant.InternalName,
        DisplayName = variant.DisplayName,
        Origin = variant.Origin,
        Changed = false,
        HashCount = variant.Hashes.Count,
    };

    private static Diagnostic UnknownBase(string baseId) => new(
        DiagnosticSeverity.Warning,
        CharacterDiagnosticCodes.UnknownBaseCharacter,
        $"There is no character called '{baseId}' yet, so this one has no family until there is. " +
        "It still shows in the grid and still takes mods.");

    private static Dictionary<string, OverlayVariant> Variants(PackOverlay overlay) =>
        new(overlay.Variants ?? new Dictionary<string, OverlayVariant>(), StringComparer.OrdinalIgnoreCase);

    private static OverlayVariant Existing(Dictionary<string, OverlayVariant> variants, string internalName) =>
        variants.GetValueOrDefault(internalName) ?? new OverlayVariant();

    /// <summary>The overridable fields an entry is authoritative for, as a set that can be added to.</summary>
    private static HashSet<string> Specified(OverlayVariant entry)
    {
        if (entry.SpecifiedFields is { } known)
        {
            return new HashSet<string>(
                known.Where(OverlayFields.IsKnown), StringComparer.OrdinalIgnoreCase);
        }

        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (field, value) in OverlayFields.ValuesOf(entry))
        {
            if (value is not null)
            {
                fields.Add(field);
            }
        }

        return fields;
    }

    private static OverlayVariant Set(
        OverlayVariant entry, HashSet<string> fields, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return entry;
        }

        fields.Add(field);
        return Write(entry, field, value.Trim());
    }

    private static OverlayVariant Apply(
        OverlayVariant entry,
        HashSet<string> fields,
        string field,
        EditField<string> edit,
        Func<string, string>? normalise = null)
    {
        if (!edit.IsSet)
        {
            return entry;
        }

        fields.Add(field);

        var value = edit.Value is { Length: > 0 } text
            ? normalise?.Invoke(text) ?? text
            : null;

        return Write(entry, field, value);
    }

    private static OverlayVariant Clear(OverlayVariant entry, string field) => Write(entry, field, null);

    private static OverlayVariant Write(OverlayVariant entry, string field, string? value) => field switch
    {
        OverlayFields.DisplayName => entry with { DisplayName = value },
        OverlayFields.BaseCharacterId => entry with { BaseCharacterId = value },
        OverlayFields.IsDefaultVariant => entry with
        {
            IsDefaultVariant = bool.TryParse(value, out var flag) ? flag : null,
        },
        OverlayFields.Aliases => value is null ? entry with { Aliases = null } : entry,
        OverlayFields.ModFilesName => entry with { ModFilesName = value },
        OverlayFields.Image => entry with { Image = value },
        OverlayFields.ReleaseDate => entry with { ReleaseDate = value },
        OverlayFields.Attributes => value is null ? entry with { Attributes = null } : entry,
        OverlayFields.Hidden => entry with { Hidden = bool.TryParse(value, out var hidden) ? hidden : null },
        OverlayFields.Notes => entry with { Notes = value },
        _ => entry,
    };

    /// <summary>Marks a variant as edited, so a hash change locks it as any other change does.</summary>
    private static PackOverlay MarkEdited(PackOverlay overlay, string internalName)
    {
        var variants = Variants(overlay);
        var entry = Existing(variants, internalName);

        variants[internalName] = entry with
        {
            SpecifiedFields = Specified(entry),
            Origin = entry.Origin ?? VariantOrigin.Modified,
            Locked = entry.Locked ?? true,
        };

        return overlay with { Variants = variants };
    }

    private static List<PackHashEntry> Normalise(IReadOnlyList<PackHashEntry>? hashes, string internalName)
    {
        var normalised = new List<PackHashEntry>();

        foreach (var entry in hashes ?? [])
        {
            if (Core.Hashes.HashText.Normalize(entry.Hash) is not { } hash)
            {
                continue;
            }

            var candidate = entry with { Variant = internalName, Hash = hash };

            if (!normalised.Any(existing => Same(existing, candidate)))
            {
                normalised.Add(candidate);
            }
        }

        return normalised;
    }

    private static bool Same(PackHashEntry left, PackHashEntry right) =>
        string.Equals(left.Variant, right.Variant, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Hash, right.Hash, StringComparison.OrdinalIgnoreCase)
        && left.Kind == right.Kind;

    /// <summary>Reports duplicates and cross-variant conflicts; they are named, never blocked.</summary>
    private static List<Diagnostic> Inspect(
        GameData data,
        string internalName,
        IReadOnlyList<PackHashEntry> incoming,
        IReadOnlyList<PackHashEntry> existing)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var entry in incoming)
        {
            if (existing.Any(candidate => string.Equals(candidate.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Info,
                    CharacterDiagnosticCodes.DuplicateHash,
                    $"{entry.Hash} is already recorded here, so it was not added twice."));

                continue;
            }

            if (entry.Kind == HashKind.RootVs)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    CharacterDiagnosticCodes.SharedShaderHash,
                    $"{entry.Hash} is a shader hash shared by many characters, so it was kept but " +
                    "will not help identify this one."));
            }

            var claimant = data.Variants.FirstOrDefault(variant =>
                !PathComparer.AreNamesEqual(variant.InternalName, internalName)
                && variant.Hashes.Any(hash =>
                    string.Equals(hash.Hash, entry.Hash, StringComparison.OrdinalIgnoreCase)));

            if (claimant is not null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    CharacterDiagnosticCodes.HashClaimedElsewhere,
                    $"{entry.Hash} is also recorded against {claimant.DisplayName}. It was kept — " +
                    "you may be correcting the pack — but the two will now compete when sorting."));
            }
        }

        return diagnostics;
    }

    private static PackOverlay WithAddedHashes(PackOverlay overlay, IReadOnlyList<PackHashEntry> hashes)
    {
        var existing = overlay.Hashes ?? new OverlayHashes();
        var add = new List<PackHashEntry>(existing.Add ?? []);

        // A hash added back must stop being removed, or the merge would subtract it again.
        var remove = (existing.Remove ?? [])
            .Where(entry => !hashes.Any(candidate => Same(entry, candidate)))
            .ToList();

        foreach (var entry in hashes)
        {
            if (!add.Any(candidate => Same(candidate, entry)))
            {
                add.Add(entry);
            }
        }

        return overlay with { Hashes = existing with { Add = add, Remove = remove } };
    }

    /// <summary>Drops every overlay hash edit and replace mode for one variant, bringing the pack's back.</summary>
    internal static PackOverlay ClearHashes(PackOverlay overlay, string internalName)
    {
        if (overlay.Hashes is not { } hashes)
        {
            return overlay;
        }

        var modes = hashes.VariantModes is { Count: > 0 } existing
            ? existing
                .Where(pair => !PathComparer.AreNamesEqual(pair.Key, internalName))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            : null;

        return overlay with
        {
            Hashes = hashes with
            {
                Add = Without(hashes.Add, internalName),
                Remove = Without(hashes.Remove, internalName),
                VariantModes = modes is { Count: > 0 } ? modes : null,
            },
        };
    }

    private static List<PackHashEntry>? Without(IReadOnlyList<PackHashEntry>? entries, string internalName)
    {
        if (entries is not { Count: > 0 })
        {
            return null;
        }

        var kept = entries
            .Where(entry => !PathComparer.AreNamesEqual(entry.Variant, internalName))
            .ToList();

        return kept.Count == 0 ? null : kept;
    }

    private static bool HasOwnHashes(PackOverlay overlay, string internalName) =>
        (overlay.Hashes?.Add ?? []).Any(entry => PathComparer.AreNamesEqual(entry.Variant, internalName))
        || (overlay.Hashes?.Remove ?? []).Any(entry => PathComparer.AreNamesEqual(entry.Variant, internalName));
}
