using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Text;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm sort run</c>, <c>undo</c> and <c>runs</c>: sorting a Mods folder, dry by default.</summary>
internal static class SortRunCommands
{
    /// <summary>Builds the subcommands.</summary>
    /// <param name="load">Loads the merged pack data for a game.</param>
    public static IEnumerable<Command> Create(
        Func<ServiceProvider, string, string?, CancellationToken, Task<GameData>> load) =>
    [
        CreateRun(load),
        CreateUndo(),
        CreateRuns(),
    ];

    private static Command CreateRun(
        Func<ServiceProvider, string, string?, CancellationToken, Task<GameData>> load)
    {
        var game = new Option<string>("--game")
        {
            Description = "The game to sort against, for example the gameId from a pack's manifest.",
            Required = true,
        };

        var pack = new Option<string?>("--pack")
        {
            Description = "Use a pack directory directly instead of the installed one.",
        };

        var mods = ModsFolderOptions.Mods();

        var apply = new Option<bool>("--apply")
        {
            Description = "Actually move the mods. Without it nothing is touched.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Show what would happen and change nothing. The default.",
        };

        var character = new Option<string?>("--character")
        {
            Description =
                "Narrow to one character: the mods filed under it that belong elsewhere, and "
                + "the mods elsewhere that belong to it. Takes an internal name.",
        };

        var matchedBy = new Option<string?>("--matched-by")
        {
            Description =
                "Only the mods found one way: hash, name (a file name or the mod's own name), or others "
                + "(nothing identified them). With --apply, only those move.",
        };

        var unsorted = new Option<bool>("--unsorted")
        {
            Description =
                "Narrow to the mods that belong to no character: loose at the top of the Mods folder, "
                + "or in a folder no character owns and that is not Others. The app's Unsorted tile.",
        };

        var others = new Option<bool>("--others")
        {
            Description = "Narrow to the mods in Others. The app's Others tile.",
        };

        var keep = new Option<string[]>("--keep")
        {
            Description =
                "A mod folder to leave where it is, as unticking its row in the app does. With --apply, a "
                + "mod under a character is remembered as filed there and never offered again; one that is "
                + "loose or in Others is just not moved this time. Repeat it for several.",
        };

        var command = new Command("run", "Sort a whole Mods folder. Shows a preview unless --apply is given.")
        {
            game,
            pack,
            mods,
            apply,
            dryRun,
            character,
            matchedBy,
            unsorted,
            others,
            keep,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            SortMatchKind? matchKind = parse.GetValue(matchedBy)?.ToLowerInvariant() switch
            {
                null or "" => null,
                "hash" => SortMatchKind.Hash,
                "name" => SortMatchKind.Name,
                "others" => SortMatchKind.Others,
                var other => throw new ModOperationException(
                    $"--matched-by takes hash, name or others, not '{other}'."),
            };

            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var shouldApply = parse.GetValue(apply) && !parse.GetValue(dryRun);

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, parse.GetValue(game)!, cancellationToken)
                .ConfigureAwait(false);

            var data = await load(provider, parse.GetValue(game)!, parse.GetValue(pack), cancellationToken)
                .ConfigureAwait(false);

            var runner = provider.GetRequiredService<ISortRunner>();
            var plan = await runner.PlanAsync(modsDirectory, data, null, cancellationToken).ConfigureAwait(false);

            if (parse.GetValue(character) is { Length: > 0 } only)
            {
                // The whole folder is planned, since a destination is right only relative to the Mods root; the rows
                // narrow.
                plan = plan.ForCharacter(data, only);
            }

            if (matchKind is { } kind)
            {
                plan = plan.Matching(kind);
            }

            if (parse.GetValue(unsorted))
            {
                plan = plan.ForUnsorted(data);
            }

            if (parse.GetValue(others))
            {
                plan = plan.ForOthers(data);
            }

            var keptRows = KeptRows(plan, parse.GetValue(keep) ?? []);

            SortRunResult? result = null;
            SortKeepResult? kept = null;

            if (shouldApply)
            {
                result = await runner
                    .ApplyAsync(plan.ModsDirectory, [.. plan.Moves.Where(row => !keptRows.Contains(row))], cancellationToken)
                    .ConfigureAwait(false);

                kept = await runner.KeepAsync([.. keptRows], data, cancellationToken).ConfigureAwait(false);
            }

            var report = BuildReport(plan, result, kept);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.Failures.Count > 0 ? 1 : 0);
            }

            WriteRunHuman(report);
            return report.Failures.Count > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command CreateUndo()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var run = new Option<string?>("--run")
        {
            Description = "The run to undo. Defaults to the most recent one that has not been undone.",
        };

        var command = new Command("undo", "Put a whole sort run back where it came from.")
        {
            mods,
            game,
            run,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);

            var result = await provider
                .GetRequiredService<ISortRunner>()
                .UndoAsync(modsDirectory, parse.GetValue(run), cancellationToken)
                .ConfigureAwait(false);

            var report = new SortUndoReport(
                result.RunId,
                result.RestoredCount,
                result.Skipped.Count,
                [
                    .. result.Outcomes.Select(outcome => new SortUndoRowReport(
                        outcome.From, outcome.To, outcome.Restored, outcome.Note)),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report, report.SkippedCount > 0 ? 1 : 0);
            }

            CliOutput.WriteRows(
            [
                ("Run", report.RunId),
                ("Put back", report.RestoredCount.ToString(CultureInfo.InvariantCulture)),
                ("Skipped", report.SkippedCount.ToString(CultureInfo.InvariantCulture)),
            ]);

            foreach (var row in report.Rows)
            {
                Console.Out.WriteLine(row.Restored
                    ? $"  ok    {row.From}  ->  {row.To}"
                    : $"  skip  {row.From}  —  {row.Note}");
            }

            return report.SkippedCount > 0 ? 1 : 0;
        });

        return command;
    }

    private static Command CreateRuns()
    {
        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();
        var command = new Command("runs", "List the sort runs recorded in a Mods folder.")
        {
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var journal = provider.GetRequiredService<ISortJournal>();

            var modsDirectory = await ModsFolderOptions
                .ResolveAsync(provider, parse, mods, game, cancellationToken)
                .ConfigureAwait(false);
            var runs = await journal.ListRunsAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

            var report = new SortRunsReport(
                modsDirectory,
                journal.GetJournalPath(modsDirectory),
                [
                    .. runs.Select(run => new SortRunSummaryReport(
                        run.RunId, run.At, run.MoveCount, run.UndoneCount, run.IsFullyUndone)),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
                ("Journal", PathDisplay.Show(report.JournalPath)),
                ("Runs", report.Runs.Count.ToString(CultureInfo.InvariantCulture)),
            ]);

            foreach (var run in report.Runs)
            {
                var state = run.FullyUndone
                    ? "undone"
                    : run.UndoneCount > 0
                        ? $"{run.UndoneCount.ToString(CultureInfo.InvariantCulture)} of " +
                          $"{run.MoveCount.ToString(CultureInfo.InvariantCulture)} put back"
                        : "undoable";

                Console.Out.WriteLine(
                    $"  {run.RunId}  {run.At.ToString("u", CultureInfo.InvariantCulture)}  " +
                    $"{EnglishCount.Plural(run.MoveCount, "move", "moves"),-10}  {state}");
            }

            return 0;
        });

        return command;
    }

    /// <summary>The planned moves <c>--keep</c> names; one that matches nothing is refused, not ignored.</summary>
    private static HashSet<SortRunRow> KeptRows(SortRunPlan plan, IReadOnlyList<string> keep)
    {
        var rows = new HashSet<SortRunRow>(ReferenceEqualityComparer.Instance);

        foreach (var folder in keep)
        {
            var full = Path.GetFullPath(folder);

            var row = plan.Moves.FirstOrDefault(candidate => PathComparer.AreEqual(candidate.Mod.Path, full))
                      ?? throw new ModOperationException(
                          $"--keep '{PathDisplay.Show(folder)}' is not one of the mods this sort would move, so there is " +
                          "nothing to keep. Run without --apply to see the list.",
                          full);

            rows.Add(row);
        }

        return rows;
    }

    private static SortRunReport BuildReport(SortRunPlan plan, SortRunResult? result, SortKeepResult? kept)
    {
        var moved = result?.Outcomes
            .Where(outcome => outcome.Succeeded)
            .ToDictionary(outcome => outcome.Row.Mod.Path, outcome => outcome.Result!.ToName, StringComparer.Ordinal);

        return new SortRunReport(
            plan.ModsDirectory,
            plan.GameId,
            result is not null,
            result?.RunId,
            plan.Rows.Count,
            plan.Moves.Count,
            result?.MovedCount ?? 0,
            [
                .. plan.Rows.Select(row => new SortRunRowReport(
                    row.Mod.Name,
                    row.Mod.VariantFolderName,
                    row.DestinationFolderName,
                    CliOutput.Camel(row.Action.ToString()),
                    row.Decision.DecidedBy.ToString().ToLowerInvariant(),
                    Math.Round(row.Decision.Confidence, 4, MidpointRounding.AwayFromZero),
                    row.VariantIsUncertain,
                    row.Reason,
                    // Only a clash's rename: a disabled mod's folder name is not one.
                    moved is not null && moved.TryGetValue(row.Mod.Path, out var name)
                                      && !string.Equals(name, Path.GetFileName(row.Mod.Path), StringComparison.Ordinal)
                        ? name
                        : null)),
            ],
            [
                .. (result?.Failures ?? []).Select(failure =>
                    new SortRunFailureReport(failure.Row.Mod.Name, failure.Error!)),
                .. (kept?.Failures ?? []).Select(failure =>
                    new SortRunFailureReport(failure.Row.Mod.Name, failure.Error)),
            ],
            [
                .. plan.Diagnostics.Select(diagnostic => new ScanDiagnostic(
                    diagnostic.Severity.ToString().ToLowerInvariant(),
                    diagnostic.Code,
                    diagnostic.Message,
                    null,
                    null)),
            ],
            [.. (kept?.Remembered ?? []).Select(row => row.Mod.Name)],
            [.. (kept?.NotRemembered ?? []).Select(row => row.Mod.Name)]);
    }

    private static void WriteRunHuman(SortRunReport report)
    {
        CliOutput.WriteRows(
        [
            ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
            ("Game", report.GameId),
            ("Mods looked at", report.ModCount.ToString(CultureInfo.InvariantCulture)),
            ("Would move", report.PlannedMoveCount.ToString(CultureInfo.InvariantCulture)),
            report.Applied
                ? ("Moved", report.MovedCount.ToString(CultureInfo.InvariantCulture))
                : ("Moved", "nothing — this is a preview. Add --apply to do it."),
            report.Applied ? ("Run", report.RunId ?? "-") : (string.Empty, string.Empty),
        ]);

        var moves = report.Rows.Where(row => row.Action == "move").ToList();

        if (moves.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Would move" + (report.Applied ? " (done)" : string.Empty));

            foreach (var row in moves)
            {
                var uncertain = row.VariantIsUncertain ? "  [outfit is a guess]" : string.Empty;
                var renamed = row.MovedTo is { Length: > 0 } name
                    ? $"  [renamed to '{name}']"
                    : string.Empty;

                Console.Out.WriteLine(
                    $"  {row.From ?? "(not filed)",-24} -> {row.To,-24} " +
                    $"{row.Mod,-34} {row.DecidedBy,-9} " +
                    $"{(row.Confidence * 100).ToString("0", CultureInfo.InvariantCulture) + "%",4}" +
                    $"{uncertain}{renamed}");
            }
        }

        if (report.KeptRemembered.Count > 0 || report.KeptNotRemembered.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Kept where they are");

            foreach (var mod in report.KeptRemembered)
            {
                Console.Out.WriteLine($"  {mod,-34} remembered as filed by you; auto-sort will leave it alone");
            }

            foreach (var mod in report.KeptNotRemembered)
            {
                Console.Out.WriteLine($"  {mod,-34} not under a character, so it will be offered again next time");
            }
        }

        var kept = report.Rows.Where(row => row.Action != "move").ToList();

        if (kept.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Left alone");

            foreach (var row in kept)
            {
                Console.Out.WriteLine($"  {row.Mod,-40} {row.Reason}");
            }
        }

        if (report.Failures.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Could not be moved");

            foreach (var failure in report.Failures)
            {
                Console.Out.WriteLine($"  {failure.Mod}: {failure.Error}");
            }
        }

        if (!report.Applied && report.PlannedMoveCount > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Nothing has been moved. Run again with --apply to do it.");
        }
    }
}
