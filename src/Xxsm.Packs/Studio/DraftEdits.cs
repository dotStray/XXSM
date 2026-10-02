using Xxsm.Core;
using Xxsm.Core.Hashes;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Model;

namespace Xxsm.Packs.Studio;

/// <summary>An edit to the game itself. Every member defaults to unchanged.</summary>
public sealed record GameEdit
{
    /// <summary>The game's name. Cannot be cleared.</summary>
    public EditField<string> DisplayName { get; init; }

    /// <summary>A short form for narrow places.</summary>
    public EditField<string> ShortName { get; init; }

    /// <summary>The XXMI importer folder name.</summary>
    public EditField<string> Importer { get; init; }

    /// <summary>The game's icon, as a pack-relative path or web address.</summary>
    public EditField<string> Icon { get; init; }

    /// <summary>The prefix that marks a disabled mod folder. Cleared goes back to the default.</summary>
    public EditField<string> DisabledPrefix { get; init; }
}

/// <summary>A character added to a draft.</summary>
/// <param name="Draft">The draft with it added.</param>
/// <param name="InternalName">The internal name it was given.</param>
/// <param name="RequestedInternalName">The name asked for, when that was taken and had to be adjusted.</param>
public sealed record DraftCharacterAdded(PackDraft Draft, string InternalName, string? RequestedInternalName);

/// <summary>A change to a draft, and anything about it worth telling a person.</summary>
/// <param name="Draft">The changed draft.</param>
/// <param name="Notes">Sentences about what else had to change to keep the draft sound. Often empty.</param>
public sealed record DraftChange(PackDraft Draft, IReadOnlyList<string> Notes);

/// <summary>A change to one character's hashes.</summary>
/// <param name="Draft">The changed draft.</param>
/// <param name="Count">How many hashes were added or removed.</param>
public sealed record DraftHashChange(PackDraft Draft, int Count);

/// <summary>A character's hashes replaced by a new set.</summary>
/// <param name="Draft">The changed draft.</param>
/// <param name="Added">How many entries were added.</param>
/// <param name="Removed">How many entries were removed.</param>
public sealed record DraftHashReplacement(PackDraft Draft, int Added, int Removed);

/// <summary>Every edit Pack Studio makes to a draft. Pure: each returns a new draft.</summary>
/// <remarks>Where there is one sound answer an edit takes it and notes it; otherwise it refuses.</remarks>
public static class DraftEdits
{
    /// <summary>Adds a character. Only a name is required.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="request">What to add; its image must already be a pack-relative path or web address.</param>
    /// <returns>The draft, and the internal name the character was given.</returns>
    /// <exception cref="ModOperationException">The name is blank, the internal name cannot be an id, or the base
    /// does not exist.</exception>
    public static DraftCharacterAdded AddCharacter(PackDraft draft, NewCharacter request)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ModOperationException("A character needs a name.");
        }

        if (request.InternalName is { } asked && !string.IsNullOrWhiteSpace(asked) && !PackDrafts.IsValidId(asked.Trim()))
        {
            throw NotAnId(asked);
        }

        var taken = draft.Variants.Select(v => v.InternalName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var proposal = CharacterNames.ProposeAmong(
            string.IsNullOrWhiteSpace(request.InternalName) ? request.DisplayName : request.InternalName.Trim(), taken);
        var id = proposal.Name;

        string? baseId = null;
        if (!string.IsNullOrWhiteSpace(request.BaseCharacterId))
        {
            baseId = RootOf(draft, Require(draft, request.BaseCharacterId).InternalName);
        }

        var variant = new PackVariant
        {
            InternalName = id,
            DisplayName = request.DisplayName.Trim(),
            BaseCharacterId = baseId,
            IsDefaultVariant = request.IsDefaultVariant ?? baseId is null,
            Aliases = Aliases(request.Aliases),
            ModFilesName = Blank(request.ModFilesName) ?? id,
            Image = Blank(request.Image),
            Attributes = request.Attributes is { Count: > 0 } attributes
                ? new Dictionary<string, AttributeValue>(attributes, StringComparer.OrdinalIgnoreCase)
                : null,
            Notes = Blank(request.Notes),
            Hidden = request.Hidden ? true : null,
        };

        var added = AddHashes(
            draft with { Variants = [.. draft.Variants, variant] },
            id,
            request.Hashes ?? []);

        return new DraftCharacterAdded(added.Draft, id, proposal.IsFree ? null : proposal.Wanted);
    }

    /// <summary>Edits one character. Fields left unchanged are untouched.</summary>
    /// <returns>The changed draft, and notes about anything a base change moved with it.</returns>
    /// <exception cref="ModOperationException">There is no such character, the name would be blank, or the base
    /// does not exist or is the character itself.</exception>
    public static DraftChange EditCharacter(PackDraft draft, string internalName, CharacterEdit edit)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(edit);

        var variants = draft.Variants.ToList();
        var index = IndexOf(variants, internalName);
        var variant = variants[index];

        if (edit.DisplayName.IsSet)
        {
            if (string.IsNullOrWhiteSpace(edit.DisplayName.Value))
            {
                throw new ModOperationException("A character needs a name.", internalName);
            }

            variant = variant with { DisplayName = edit.DisplayName.Value.Trim() };
        }

        if (edit.Aliases.IsSet)
        {
            variant = variant with { Aliases = Aliases(edit.Aliases.Value) };
        }

        if (edit.ModFilesName.IsSet)
        {
            variant = variant with { ModFilesName = Blank(edit.ModFilesName.Value) };
        }

        if (edit.Image.IsSet)
        {
            variant = variant with { Image = Blank(edit.Image.Value) };
        }

        if (edit.Attributes.IsSet)
        {
            variant = variant with
            {
                Attributes = edit.Attributes.Value is { Count: > 0 } attributes
                    ? new Dictionary<string, AttributeValue>(attributes, StringComparer.OrdinalIgnoreCase)
                    : null,
            };
        }

        if (edit.Hidden.IsSet)
        {
            variant = variant with { Hidden = edit.Hidden.Value ? true : null };
        }

        if (edit.Notes.IsSet)
        {
            variant = variant with { Notes = Blank(edit.Notes.Value) };
        }

        if (edit.IsDefaultVariant.IsSet)
        {
            variant = variant with { IsDefaultVariant = edit.IsDefaultVariant.Value };
        }

        variants[index] = variant;
        var edited = draft with { Variants = variants };

        if (!edit.BaseCharacterId.IsSet)
        {
            return new DraftChange(edited, []);
        }

        var linked = SetBase(edited, [variant.InternalName], Blank(edit.BaseCharacterId.Value));

        return edit.IsDefaultVariant.IsSet
            ? linked with { Draft = SetField(linked.Draft, variant.InternalName, v => v with { IsDefaultVariant = edit.IsDefaultVariant.Value }) }
            : linked;
    }

    /// <summary>Gives a character a new internal name, carrying its hashes and its outfits' links.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalName">The character.</param>
    /// <param name="newInternalName">Its new internal name. Changing only its capitals is allowed.</param>
    /// <exception cref="ModOperationException">There is no such character, the new name cannot be an id, or
    /// another character has it.</exception>
    public static PackDraft RenameCharacter(PackDraft draft, string internalName, string newInternalName)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(newInternalName);

        var to = newInternalName.Trim();

        if (!PackDrafts.IsValidId(to))
        {
            throw NotAnId(to);
        }

        var variants = draft.Variants.ToList();
        var index = IndexOf(variants, internalName);
        var from = variants[index].InternalName;

        if (variants.Find(v => !ReferenceEquals(v, variants[index]) && Same(v.InternalName, to)) is { } clash)
        {
            throw new ModOperationException(
                $"'{clash.InternalName}' is already the internal name of '{clash.DisplayName}'.", internalName);
        }

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];

            if (i == index)
            {
                variants[i] = variant with
                {
                    InternalName = to,
                    ModFilesName = variant.ModFilesName is null || Same(variant.ModFilesName, from) ? to : variant.ModFilesName,
                };
            }
            else if (Same(variant.BaseCharacterId, from))
            {
                variants[i] = variant with { BaseCharacterId = to };
            }
        }

        var entries = (draft.Hashes.Entries ?? [])
            .Select(e => Same(e.Variant, from) ? e with { Variant = to } : e)
            .ToList();

        return draft with { Variants = variants, Hashes = draft.Hashes with { Entries = entries } };
    }

    /// <summary>Deletes characters and their hashes. Their outfits become characters in their own right.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalNames">The characters to delete. Names not in the draft are ignored.</param>
    public static DraftChange DeleteCharacters(PackDraft draft, IReadOnlyCollection<string> internalNames)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(internalNames);

        var doomed = internalNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        var kept = new List<PackVariant>();

        foreach (var variant in draft.Variants)
        {
            if (doomed.Contains(variant.InternalName))
            {
                continue;
            }

            if (variant.BaseCharacterId is { } baseId && doomed.Contains(baseId))
            {
                notes.Add($"'{variant.DisplayName}' was an outfit of '{baseId}', so it is a character in its own right now.");
                kept.Add(variant with { BaseCharacterId = null, IsDefaultVariant = true });
                continue;
            }

            kept.Add(variant);
        }

        var entries = (draft.Hashes.Entries ?? []).Where(e => !doomed.Contains(e.Variant ?? string.Empty)).ToList();

        return new DraftChange(draft with { Variants = kept, Hashes = draft.Hashes with { Entries = entries } }, notes);
    }

    /// <summary>Makes characters outfits of a base, or with none, characters in their own right.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalNames">The characters to link.</param>
    /// <param name="baseCharacterId">The base, or null to unlink.</param>
    /// <returns>The changed draft, and notes about anything that had to move with it.</returns>
    /// <exception cref="ModOperationException">A character or the base does not exist, or a character would be
    /// its own outfit.</exception>
    public static DraftChange SetBase(PackDraft draft, IReadOnlyCollection<string> internalNames, string? baseCharacterId)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(internalNames);

        var variants = draft.Variants.ToList();
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(baseCharacterId))
        {
            foreach (var name in internalNames)
            {
                var index = IndexOf(variants, name);

                if (variants[index].BaseCharacterId is not null)
                {
                    variants[index] = variants[index] with { BaseCharacterId = null, IsDefaultVariant = true };
                }
            }

            return new DraftChange(draft with { Variants = variants }, notes);
        }

        var requested = variants[IndexOf(variants, baseCharacterId)];
        var root = RootOf(draft, requested.InternalName);

        if (!Same(root, requested.InternalName))
        {
            notes.Add($"'{requested.DisplayName}' is itself an outfit of '{root}', so they were linked to '{root}' instead.");
        }

        foreach (var name in internalNames)
        {
            var index = IndexOf(variants, name);
            var variant = variants[index];

            if (Same(variant.InternalName, root))
            {
                throw new ModOperationException($"'{variant.DisplayName}' cannot be an outfit of itself.", name);
            }

            variants[index] = variant with { BaseCharacterId = root, IsDefaultVariant = false };

            // An outfit of an outfit cannot be held: the character's own outfits go to the same base.
            for (var i = 0; i < variants.Count; i++)
            {
                if (i != index && Same(variants[i].BaseCharacterId, variant.InternalName))
                {
                    notes.Add(
                        $"'{variants[i].DisplayName}' was an outfit of '{variant.DisplayName}', so it is an outfit of '{root}' now too.");
                    variants[i] = variants[i] with { BaseCharacterId = root, IsDefaultVariant = false };
                }
            }
        }

        return new DraftChange(draft with { Variants = variants }, notes);
    }

    /// <summary>Makes one character its family's default outfit, and no other.</summary>
    /// <exception cref="ModOperationException">There is no such character.</exception>
    public static PackDraft SetDefault(PackDraft draft, string internalName)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var chosen = Require(draft, internalName);
        var family = RootOf(draft, chosen.InternalName);

        return draft with
        {
            Variants =
            [
                .. draft.Variants.Select(v =>
                    Same(v.InternalName, family) || Same(v.BaseCharacterId, family)
                        ? v with { IsDefaultVariant = Same(v.InternalName, chosen.InternalName) }
                        : v),
            ],
        };
    }

    /// <summary>Reads a value typed for an attribute: a number, a list on commas or semicolons, or one value.</summary>
    /// <param name="draft">The draft, whose game declares the attribute.</param>
    /// <param name="attributeId">The attribute's id. Capitals do not matter.</param>
    /// <param name="text">What was typed.</param>
    /// <returns>The value, or null when nothing was typed, which clears the attribute.</returns>
    /// <exception cref="ModOperationException">The game has no such attribute, or it holds a number and the text
    /// is not one.</exception>
    public static AttributeValue? ParseAttributeValue(PackDraft draft, string attributeId, string? text)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var attributes = draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>();
        var id = attributes.Keys.FirstOrDefault(k => Same(k, attributeId))
                 ?? throw new ModOperationException($"The game has no attribute called '{attributeId}'. Add it to the game first.");

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var definition = attributes[id];

        if (definition.IsNumeric)
        {
            if (parts.Length != 1
                || !double.TryParse(
                    parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var number))
            {
                throw new ModOperationException(
                    $"'{text.Trim()}' is not a number, and {definition.DisplayName ?? id} holds a number, such as 4 or 4.5.");
            }

            return AttributeValue.FromNumber(number);
        }

        return parts.Length switch
        {
            0 => null,
            1 => AttributeValue.FromString(parts[0]),
            _ => AttributeValue.FromArray(parts),
        };
    }

    /// <summary>Sets one attribute on characters, or clears it.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalNames">The characters.</param>
    /// <param name="attributeId">An attribute the game declares.</param>
    /// <param name="value">The value, or null to clear it.</param>
    /// <exception cref="ModOperationException">The game has no such attribute, or a character does not
    /// exist.</exception>
    public static PackDraft SetAttribute(
        PackDraft draft,
        IReadOnlyCollection<string> internalNames,
        string attributeId,
        AttributeValue? value)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(internalNames);

        var declared = (draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>())
            .Keys
            .FirstOrDefault(k => Same(k, attributeId))
            ?? throw new ModOperationException($"The game has no attribute called '{attributeId}'. Add it to the game first.");

        var variants = draft.Variants.ToList();

        foreach (var name in internalNames)
        {
            var index = IndexOf(variants, name);
            var attributes = new Dictionary<string, AttributeValue>(
                variants[index].Attributes ?? new Dictionary<string, AttributeValue>(), StringComparer.OrdinalIgnoreCase);

            attributes.Remove(declared);

            if (value is not null)
            {
                attributes[declared] = value;
            }

            variants[index] = variants[index] with { Attributes = attributes.Count == 0 ? null : attributes };
        }

        return draft with { Variants = variants };
    }

    /// <summary>Adds an alias of each character's name with a prefix and suffix: <c>CN </c> + Ganyu.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalNames">The characters.</param>
    /// <param name="prefix">What goes before the name, or null.</param>
    /// <param name="suffix">What goes after the name, or null.</param>
    /// <returns>The changed draft. A character that already has that alias is left alone.</returns>
    /// <exception cref="ModOperationException">Both are blank, or a character does not exist.</exception>
    public static PackDraft AddAlias(PackDraft draft, IReadOnlyCollection<string> internalNames, string? prefix, string? suffix)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(internalNames);

        if (string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(suffix))
        {
            throw new ModOperationException("An alias needs something before or after the name.");
        }

        var variants = draft.Variants.ToList();

        foreach (var name in internalNames)
        {
            var index = IndexOf(variants, name);
            var variant = variants[index];
            var alias = $"{prefix}{variant.DisplayName}{suffix}".Trim();
            var aliases = (variant.Aliases ?? []).ToList();

            if (!aliases.Exists(a => Same(a, alias)))
            {
                aliases.Add(alias);
                variants[index] = variant with { Aliases = aliases };
            }
        }

        return draft with { Variants = variants };
    }

    /// <summary>Adds hashes to a character; ones it has, and anything not a hash, are skipped.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalName">The character.</param>
    /// <param name="hashes">The hashes, normally from <see cref="Hashes.HashPaste"/>. Their variant is ignored.</param>
    /// <exception cref="ModOperationException">There is no such character.</exception>
    public static DraftHashChange AddHashes(PackDraft draft, string internalName, IReadOnlyList<PackHashEntry> hashes)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(hashes);

        var id = Require(draft, internalName).InternalName;
        var entries = (draft.Hashes.Entries ?? []).ToList();
        var have = entries.Where(e => Same(e.Variant, id)).Select(e => (e.Hash.ToLowerInvariant(), e.Kind)).ToHashSet();
        var added = 0;

        foreach (var entry in hashes)
        {
            if (HashText.Normalize(entry.Hash) is { } hash && have.Add((hash, entry.Kind)))
            {
                entries.Add(entry with { Variant = id, Hash = hash });
                added++;
            }
        }

        return new DraftHashChange(added == 0 ? draft : draft with { Hashes = draft.Hashes with { Entries = entries } }, added);
    }

    /// <summary>Removes hashes from a character, in any capitalisation and of any kind.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalName">The character.</param>
    /// <param name="hashes">The hash values to remove.</param>
    /// <exception cref="ModOperationException">There is no such character.</exception>
    public static DraftHashChange RemoveHashes(PackDraft draft, string internalName, IReadOnlyCollection<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(hashes);

        var id = Require(draft, internalName).InternalName;
        var targets = hashes.Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = (draft.Hashes.Entries ?? []).ToList();
        var removed = entries.RemoveAll(e => Same(e.Variant, id) && targets.Contains(e.Hash));

        return new DraftHashChange(removed == 0 ? draft : draft with { Hashes = draft.Hashes with { Entries = entries } }, removed);
    }

    /// <summary>Makes a character's hashes exactly these; one it already has keeps its kind.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalName">The character.</param>
    /// <param name="hashes">The hashes to keep, normally from <see cref="Hashes.HashPaste"/>. Their variant is
    /// ignored.</param>
    /// <exception cref="ModOperationException">There is no such character.</exception>
    public static DraftHashReplacement ReplaceHashes(PackDraft draft, string internalName, IReadOnlyList<PackHashEntry> hashes)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(hashes);

        var id = Require(draft, internalName).InternalName;
        var wanted = hashes
            .Select(e => HashText.Normalize(e.Hash))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var have = (draft.Hashes.Entries ?? [])
            .Where(e => Same(e.Variant, id))
            .Select(e => e.Hash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = RemoveHashes(draft, id, [.. have.Where(h => !wanted.Contains(h))]);
        var added = AddHashes(
            removed.Draft,
            id,
            [.. hashes.Where(e => HashText.Normalize(e.Hash) is { } hash && !have.Contains(hash))]);

        return new DraftHashReplacement(added.Draft, added.Count, removed.Count);
    }

    /// <summary>Keeps hashes on one family: every character outside it loses them; its outfits keep theirs.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="family">The family to keep them on, by any of its characters' internal names.</param>
    /// <param name="hashes">The hashes, in any capitals.</param>
    /// <exception cref="ModOperationException">There is no such character, or its family carries none of the
    /// hashes.</exception>
    public static DraftHashChange KeepHashesOn(PackDraft draft, string family, IReadOnlyCollection<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(hashes);

        var keeper = Require(draft, family);
        var kept = PackValidator.KeptVariants(draft);
        var head = kept.TryGetValue(keeper.InternalName, out var known) ? PackValidator.FamilyOf(known, kept) : keeper.InternalName;
        var targets = hashes.Select(HashText.Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var entries = (draft.Hashes.Entries ?? []).ToList();

        bool InFamily(string? variant) =>
            kept.TryGetValue(variant ?? string.Empty, out var found) && Same(PackValidator.FamilyOf(found, kept), head);

        bool Targeted(PackHashEntry entry) => HashText.Normalize(entry.Hash) is { } hash && targets.Contains(hash);

        if (!entries.Any(e => Targeted(e) && InFamily(e.Variant)))
        {
            throw new ModOperationException(
                $"'{keeper.DisplayName ?? keeper.InternalName}' does not carry {(targets.Count == 1 ? "that hash" : "those hashes")}, " +
                "so keeping them there would leave nobody with them.");
        }

        var removed = entries.RemoveAll(e => Targeted(e) && !InFamily(e.Variant));

        return new DraftHashChange(removed == 0 ? draft : draft with { Hashes = draft.Hashes with { Entries = entries } }, removed);
    }

    /// <summary>Puts hashes on the pack's ignore list, so the sorter never scores them; characters keep them.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="hashes">The hashes, in any capitals. Ones already listed are skipped.</param>
    /// <exception cref="ModOperationException">One of them is not a hash.</exception>
    public static DraftHashChange IgnoreHashes(PackDraft draft, IReadOnlyCollection<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(hashes);

        var list = (draft.Hashes.IgnoredHashes ?? []).ToList();
        var have = list.Select(HashText.Normalize).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var raw in hashes)
        {
            var hash = HashText.Normalize(raw)
                       ?? throw new ModOperationException($"'{raw}' is not a hash — a hash is 8 or 16 of the characters 0–9 and a–f.");

            if (have.Add(hash))
            {
                list.Add(hash);
                added++;
            }
        }

        return new DraftHashChange(added == 0 ? draft : draft with { Hashes = draft.Hashes with { IgnoredHashes = list } }, added);
    }

    /// <summary>Takes entries off the pack's ignore list, exactly as written there, well-formed or not.</summary>
    public static DraftHashChange UnignoreHashes(PackDraft draft, IReadOnlyCollection<string> entries)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(entries);

        var targets = entries.Select(e => e.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = (draft.Hashes.IgnoredHashes ?? []).ToList();
        var removed = list.RemoveAll(e => targets.Contains(e.Trim()));

        return new DraftHashChange(removed == 0 ? draft : draft with { Hashes = draft.Hashes with { IgnoredHashes = list } }, removed);
    }

    /// <summary>Deletes the hashes filed under an internal name no character has.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="internalName">The name the hashes are filed under.</param>
    /// <exception cref="ModOperationException">A character has that name.</exception>
    public static DraftHashChange RemoveUnclaimedHashes(PackDraft draft, string internalName)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(internalName);

        if (draft.Variants.Any(v => Same(v.InternalName, internalName)))
        {
            throw new ModOperationException(
                $"'{internalName}' is a character; remove hashes from it rather than deleting them all.");
        }

        var entries = (draft.Hashes.Entries ?? []).ToList();
        var removed = entries.RemoveAll(e => Same(e.Variant, internalName));

        return new DraftHashChange(removed == 0 ? draft : draft with { Hashes = draft.Hashes with { Entries = entries } }, removed);
    }

    /// <summary>Adds one value to a listed attribute, such as <c>plasma</c> to an element.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="attributeId">The attribute.</param>
    /// <param name="valueId">The value's id. Its label is left for the attribute editor.</param>
    /// <returns>The changed draft, or the same draft when the value is listed already.</returns>
    /// <exception cref="ModOperationException">There is no such attribute, it holds a number, or the value
    /// cannot be an id.</exception>
    public static PackDraft AddAttributeValue(PackDraft draft, string attributeId, string valueId)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var (key, definition) = (draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>())
            .FirstOrDefault(a => Same(a.Key, attributeId));

        if (definition is null)
        {
            throw new ModOperationException($"The game has no attribute called '{attributeId}'.");
        }

        if (definition.IsNumeric)
        {
            throw new ModOperationException(
                $"{definition.DisplayName ?? key} holds a number, so it has no list of values to add to.");
        }

        var id = Blank(valueId) ?? throw NotAnId(valueId, "a value id");

        if (!PackDrafts.IsValidId(id))
        {
            throw NotAnId(id, "a value id");
        }

        var values = (definition.Values ?? []).ToList();

        if (values.Any(v => Same(v.Id, id)))
        {
            return draft;
        }

        values.Add(new AttributeValueDefinition { Id = id });

        return SetAttributeDefinition(draft, key, definition with { Values = values });
    }

    /// <summary>Edits the game's own details.</summary>
    /// <exception cref="ModOperationException">The name would be blank.</exception>
    public static PackDraft EditGame(PackDraft draft, GameEdit edit)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(edit);

        var game = draft.Game;

        if (edit.DisplayName.IsSet)
        {
            game = game with
            {
                DisplayName = Blank(edit.DisplayName.Value) ?? throw new ModOperationException("A game needs a name."),
            };
        }

        if (edit.ShortName.IsSet)
        {
            game = game with { ShortName = Blank(edit.ShortName.Value) };
        }

        if (edit.Importer.IsSet)
        {
            game = game with { Importer = Blank(edit.Importer.Value) };
        }

        if (edit.Icon.IsSet)
        {
            game = game with { Icon = Blank(edit.Icon.Value) };
        }

        if (edit.DisabledPrefix.IsSet)
        {
            game = game with { DisabledPrefix = Blank(edit.DisabledPrefix.Value) ?? PackSchema.DefaultDisabledPrefix };
        }

        return draft with { Game = game };
    }

    /// <summary>Adds an attribute to the game, or replaces one in place.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="attributeId">The attribute's id, such as <c>element</c>.</param>
    /// <param name="definition">Its label and, for an enumerated one, its values.</param>
    /// <exception cref="ModOperationException">The id, or one of its value ids, cannot be an id.</exception>
    public static PackDraft SetAttributeDefinition(PackDraft draft, string attributeId, AttributeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(definition);

        var id = Blank(attributeId) ?? throw NotAnId(attributeId, "an attribute id");
        if (!PackDrafts.IsValidId(id))
        {
            throw NotAnId(id, "an attribute id");
        }

        if ((definition.Values ?? []).FirstOrDefault(v => !PackDrafts.IsValidId(v.Id)) is { } bad)
        {
            throw NotAnId(bad.Id, "a value id");
        }

        var attributes = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
        var replaced = false;

        foreach (var (key, existing) in draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>())
        {
            if (Same(key, id))
            {
                attributes[key] = definition;
                replaced = true;
            }
            else
            {
                attributes[key] = existing;
            }
        }

        if (!replaced)
        {
            attributes[id] = definition;
        }

        return draft with { Game = draft.Game with { Attributes = attributes } };
    }

    /// <summary>Puts the game's attributes in a new order: filter rows and Studio columns.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="attributeIds">The attributes to lead the list; any not named follow in their old order.</param>
    /// <returns>The changed draft, or the same draft when the order is already this one.</returns>
    /// <exception cref="ModOperationException">An id is not one of the game's attributes, or is named
    /// twice.</exception>
    public static PackDraft OrderAttributes(PackDraft draft, IReadOnlyList<string> attributeIds)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(attributeIds);

        var declared = draft.Game.Attributes ?? new Dictionary<string, AttributeDefinition>();
        var ordered = new List<string>(declared.Count);

        foreach (var wanted in attributeIds)
        {
            var key = declared.Keys.FirstOrDefault(k => Same(k, wanted))
                      ?? throw new ModOperationException(
                          $"The game has no attribute called '{wanted}'. Its attributes are: " +
                          $"{(declared.Count == 0 ? "none" : string.Join(", ", declared.Keys))}.");

            if (ordered.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                throw new ModOperationException($"'{wanted}' is named twice.");
            }

            ordered.Add(key);
        }

        ordered.AddRange(declared.Keys.Where(k => !ordered.Contains(k, StringComparer.OrdinalIgnoreCase)));

        if (ordered.SequenceEqual(declared.Keys, StringComparer.Ordinal))
        {
            return draft;
        }

        var attributes = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ordered)
        {
            attributes[key] = declared[key];
        }

        return draft with { Game = draft.Game with { Attributes = attributes } };
    }

    /// <summary>Removes an attribute from the game and from every character that set it.</summary>
    /// <returns>The changed draft, or the same draft when the game has no such attribute.</returns>
    public static PackDraft RemoveAttributeDefinition(PackDraft draft, string attributeId)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Game.Attributes is not { } declared || !declared.Keys.Any(k => Same(k, attributeId)))
        {
            return draft;
        }

        var attributes = declared
            .Where(pair => !Same(pair.Key, attributeId))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var variants = draft.Variants.Select(v =>
        {
            if (v.Attributes is not { } values || !values.Keys.Any(k => Same(k, attributeId)))
            {
                return v;
            }

            var kept = values.Where(pair => !Same(pair.Key, attributeId))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return v with { Attributes = kept.Count == 0 ? null : kept };
        });

        return draft with
        {
            Game = draft.Game with { Attributes = attributes.Count == 0 ? null : attributes },
            Variants = [.. variants],
        };
    }

    /// <summary>The root of a character's family: its base, or itself.</summary>
    private static string RootOf(PackDraft draft, string internalName)
    {
        var variant = Require(draft, internalName);

        return variant.BaseCharacterId is { } parent
               && !Same(parent, variant.InternalName)
               && draft.Variants.FirstOrDefault(v => Same(v.InternalName, parent)) is { } found
            ? found.InternalName
            : variant.InternalName;
    }

    private static PackDraft SetField(PackDraft draft, string internalName, Func<PackVariant, PackVariant> change)
    {
        var variants = draft.Variants.ToList();
        var index = IndexOf(variants, internalName);
        variants[index] = change(variants[index]);
        return draft with { Variants = variants };
    }

    private static PackVariant Require(PackDraft draft, string internalName) =>
        draft.Variants.FirstOrDefault(v => Same(v.InternalName, internalName))
        ?? throw new ModOperationException($"There is no character called '{internalName}' in this draft.", internalName);

    private static int IndexOf(List<PackVariant> variants, string internalName)
    {
        var index = variants.FindIndex(v => Same(v.InternalName, internalName));

        return index >= 0
            ? index
            : throw new ModOperationException($"There is no character called '{internalName}' in this draft.", internalName);
    }

    private static List<string>? Aliases(IEnumerable<string>? aliases)
    {
        var list = (aliases ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list.Count == 0 ? null : list;
    }

    private static ModOperationException NotAnId(string value, string what = "an internal name") =>
        new($"'{value}' cannot be {what} — only letters, digits, underscores and hyphens are allowed.");

    private static bool Same(string? one, string? two) => string.Equals(one, two, StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
