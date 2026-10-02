using Serilog;
using Xxsm.Core.Mods;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Randomising;

/// <summary>One character the randomiser would change, and the mod it chose.</summary>
/// <param name="Character">The character whose folder this is.</param>
/// <param name="Folder">The character's folder, as scanned.</param>
/// <param name="Chosen">The mod that will be the one switched on.</param>
public sealed record RandomiserRow(MergedVariant Character, VariantFolder Folder, InstalledMod Chosen)
{
    /// <summary>What applying this row switches: the chosen mod on, every other one off.</summary>
    public IReadOnlyList<ModSwitch> Switches =>
    [
        .. Folder.Mods
            .Where(mod => IsChosen(mod) != mod.IsEnabled)
            .Select(mod => new ModSwitch(mod.Path, IsChosen(mod))),
    ];

    /// <summary>The mods that are switched on now and would be switched off.</summary>
    public IReadOnlyList<InstalledMod> SwitchedOff => [.. Folder.Mods.Where(mod => mod.IsEnabled && !IsChosen(mod))];

    /// <summary>Whether this row changes anything.</summary>
    public bool ChangesAnything => Folder.Mods.Any(mod => IsChosen(mod) != mod.IsEnabled);

    private bool IsChosen(InstalledMod mod) => ReferenceEquals(mod, Chosen);
}

/// <summary>What a randomiser run would do.</summary>
public sealed record RandomiserPlan
{
    /// <summary>The Mods folder.</summary>
    public required string ModsDirectory { get; init; }

    /// <summary>One row per character with at least one mod, in folder order.</summary>
    public required IReadOnlyList<RandomiserRow> Rows { get; init; }
}

/// <summary>Switches on one mod per character, chosen at random from all of its mods, and the others off.</summary>
/// <remarks><c>Others</c>, unowned folders and loose mods are left alone; skins are chosen separately.</remarks>
public interface IModRandomiser
{
    /// <summary>Chooses a mod for each character. Changes nothing.</summary>
    /// <param name="inventory">What is on disk.</param>
    /// <param name="data">The merged pack and overlay, which says which folders are characters.</param>
    /// <param name="random">The source of chance. A seeded one gives the same choice every time.</param>
    /// <param name="onlyFolders">Character folder names to limit it to, such as one character's page. <c>null</c> for
    /// every character.</param>
    RandomiserPlan Plan(
        ModsInventory inventory,
        GameData data,
        Random random,
        IReadOnlyCollection<string>? onlyFolders = null);

    /// <summary>Chooses again for one row, never the same mod when there is another to choose.</summary>
    /// <param name="row">The row to choose again for.</param>
    /// <param name="random">The source of chance.</param>
    /// <returns>The row with a different mod chosen, or the same row when the character has only one.</returns>
    RandomiserRow Reroll(RandomiserRow row, Random random);

    /// <summary>Makes the switches the given rows need, as one undoable run.</summary>
    /// <param name="modsDirectory">The Mods folder.</param>
    /// <param name="rows">The rows to apply — the plan's, less any the user unticked.</param>
    /// <param name="cancellationToken">Cancels between mods; what was switched stays switched and undoable.</param>
    /// <exception cref="Xxsm.Core.ModOperationException">The switch journal could not be written.</exception>
    Task<ModSwitchRunResult> ApplyAsync(
        string modsDirectory,
        IReadOnlyList<RandomiserRow> rows,
        CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IModRandomiser"/>.</summary>
public sealed class ModRandomiser(IModSwitcher switcher, ILogger logger) : IModRandomiser
{
    private readonly IModSwitcher _switcher = switcher;
    private readonly ILogger _logger = logger.ForContext<ModRandomiser>();

    /// <inheritdoc />
    public RandomiserPlan Plan(
        ModsInventory inventory,
        GameData data,
        Random random,
        IReadOnlyCollection<string>? onlyFolders = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(random);

        var rows = new List<RandomiserRow>();

        foreach (var folder in inventory.VariantFolders)
        {
            if (folder.Mods.Count == 0 || UnsortedMods.CharacterFor(folder.Name, data) is not { } character)
            {
                continue;
            }

            if (onlyFolders is not null &&
                !onlyFolders.Any(name => Xxsm.Core.Io.PathComparer.AreNamesEqual(name, folder.Name)))
            {
                continue;
            }

            rows.Add(new RandomiserRow(character, folder, folder.Mods[random.Next(folder.Mods.Count)]));
        }

        return new RandomiserPlan
        {
            ModsDirectory = inventory.ModsDirectory,
            Rows = rows,
        };
    }

    /// <inheritdoc />
    public RandomiserRow Reroll(RandomiserRow row, Random random)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(random);

        var others = row.Folder.Mods.Where(mod => !ReferenceEquals(mod, row.Chosen)).ToList();

        return others.Count == 0 ? row : row with { Chosen = others[random.Next(others.Count)] };
    }

    /// <inheritdoc />
    public async Task<ModSwitchRunResult> ApplyAsync(
        string modsDirectory,
        IReadOnlyList<RandomiserRow> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = await _switcher
            .ApplyAsync(modsDirectory, [.. rows.SelectMany(row => row.Switches)], ModSwitchSource.Randomiser, null, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information(
            "Randomised {Characters} characters in {ModsDirectory}: {On} switched on, {Off} switched off",
            rows.Count,
            modsDirectory,
            result.EnabledCount,
            result.DisabledCount);

        return result;
    }
}
