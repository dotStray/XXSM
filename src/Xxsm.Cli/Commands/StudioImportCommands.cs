using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Text;
using Xxsm.Packs.Studio;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm studio import</c>: the four bulk importers, each previewing first.</summary>
internal static class StudioImportCommands
{
    /// <summary>Builds the command.</summary>
    public static Command Create() =>
        new("import", "Bring characters into a draft from an assets repository, a Mods folder, pictures or a spreadsheet.")
        {
            CreateAssets(),
            CreateMods(),
            CreatePictures(),
            CreateSheet(),
        };

    private static Option<bool> DryRun() => new("--dry-run") { Description = "Show what would be imported without changing the draft." };

    private static Command CreateAssets()
    {
        var game = StudioCommand.GameArgument();
        var source = new Argument<string>("source")
        {
            Description = "An assets repository: its folder, or the zip GitHub's Download ZIP button gives.",
        };
        var dryRun = DryRun();
        var everything = new Option<bool>("--all")
        {
            Description = "Also import rows that start unticked, such as weapons and other data folders.",
        };

        var command = new Command("assets", "Import characters and their hashes from a model-importer assets repository.")
        {
            game, source, dryRun, everything,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var importer = provider.GetRequiredService<IHashAssetsImporter>();
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var plan = await importer.PlanAsync(draft, Path.GetFullPath(parse.GetValue(source)!), cancellationToken).ConfigureAwait(false);

            return await FinishCharacterImportAsync(
                    provider,
                    parse,
                    draft,
                    plan,
                    parse.GetValue(everything) ? AllChoices(plan) : null,
                    choices => importer.Apply(draft, plan, choices, StudioCommand.Now(provider)),
                    parse.GetValue(dryRun),
                    cancellationToken)
                .ConfigureAwait(false);
        });

        return command;
    }

    private static Command CreateMods()
    {
        var game = StudioCommand.GameArgument();
        var source = new Argument<string>("folder") { Description = "A Mods folder with a folder for each character." };
        var dryRun = DryRun();

        var command = new Command("mods", "Import a character for each character folder in a Mods folder, with the hashes its mods use.")
        {
            game, source, dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var importer = provider.GetRequiredService<IModsFolderImporter>();
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var plan = await importer.PlanAsync(draft, Path.GetFullPath(parse.GetValue(source)!), cancellationToken).ConfigureAwait(false);

            return await FinishCharacterImportAsync(
                    provider,
                    parse,
                    draft,
                    plan,
                    null,
                    choices => importer.Apply(draft, plan, choices, StudioCommand.Now(provider)),
                    parse.GetValue(dryRun),
                    cancellationToken)
                .ConfigureAwait(false);
        });

        return command;
    }

    private static Command CreatePictures()
    {
        var game = StudioCommand.GameArgument();
        var source = new Argument<string>("folder") { Description = "A folder of PNG, JPEG or WebP pictures named after the characters." };
        var dryRun = DryRun();
        var replace = new Option<bool>("--replace") { Description = "Also replace portraits characters already have." };

        var command = new Command("pictures", "Give characters portraits from a folder of pictures, matched by file name.")
        {
            game, source, dryRun, replace,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var importer = provider.GetRequiredService<IPortraitFolderImporter>();
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var plan = await importer.PlanAsync(draft, Path.GetFullPath(parse.GetValue(source)!), cancellationToken).ConfigureAwait(false);

            IReadOnlyList<PortraitImportChoice>? choices = parse.GetValue(replace)
                ? [.. plan.Rows.Where(r => r.InternalName is not null && r.CurrentImage is not null && r.Match != PortraitMatch.None)
                    .Select(r => new PortraitImportChoice { SourceFile = r.SourceFile, Include = true })]
                : null;

            var rows = plan.Rows
                .Select(r => new StudioImportRowReport(
                    r.SourceFile,
                    r.FileName,
                    r.InternalName,
                    CliOutput.Camel(r.Match.ToString()),
                    choices?.Any(c => c.SourceFile == r.SourceFile) == true || r.SelectedByDefault,
                    null,
                    null,
                    null,
                    null,
                    r.Note is null ? [] : [r.Note]))
                .ToList();

            var outcome = parse.GetValue(dryRun)
                ? null
                : await importer.ApplyAsync(draft, plan, choices, StudioCommand.Now(provider), cancellationToken).ConfigureAwait(false);

            if (outcome is { Changed: true })
            {
                await StudioCommand.SaveAsync(provider, outcome.Draft, cancellationToken).ConfigureAwait(false);
            }

            if (WriteJson(parse, draft, plan.Source, outcome, rows, [.. plan.Diagnostics.Select(d => d.Message)]))
            {
                return 0;
            }

            foreach (var row in rows)
            {
                Console.Out.WriteLine($"{(row.Included ? "[x]" : "[ ]")} {row.Name}  →  {row.InternalName ?? "nobody"}  ({row.Action})");

                foreach (var note in row.Notes)
                {
                    Console.Out.WriteLine($"      {note}");
                }
            }

            foreach (var diagnostic in plan.Diagnostics)
            {
                Console.Out.WriteLine(diagnostic.Message);
            }

            if (plan.Unmatched.Any())
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"Give a picture to a character by hand with: xxsm studio character picture {draft.GameId} <character> <file>");
            }

            WriteOutcome(outcome);
            return 0;
        });

        return command;
    }

    private static Command CreateSheet()
    {
        var game = StudioCommand.GameArgument();
        var source = new Argument<string>("file") { Description = "A CSV or TSV file with a header row." };
        var dryRun = DryRun();
        var columns = new Option<string[]>("--column")
        {
            Description =
                "Say what a column is for, as NUMBER=FIELD, counting from 1. FIELD is name, internalName, base, " +
                "default, aliases, modFilesName, releaseDate, notes, hidden, image, ignore, or one of the game's attributes. " +
                "May be given more than once; other columns are guessed from their headers.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("sheet", "Import or update characters from a spreadsheet saved as CSV or TSV.")
        {
            game, source, dryRun, columns,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var importer = provider.GetRequiredService<ISpreadsheetImporter>();
            var draft = await StudioCommand.LoadAsync(provider, parse.GetValue(game)!, cancellationToken).ConfigureAwait(false);
            var plan = await importer.PlanAsync(draft, Path.GetFullPath(parse.GetValue(source)!), cancellationToken: cancellationToken).ConfigureAwait(false);

            if (parse.GetValue(columns) is { Length: > 0 } overrides)
            {
                var mapping = plan.Columns.ToList();

                foreach (var text in overrides)
                {
                    if (ParseColumn(text, mapping.Count, draft) is not { } column)
                    {
                        CliOutput.WriteError($"'{text}' is not NUMBER=FIELD for one of the {StudioCommand.Count(mapping.Count)} columns.");
                        return 1;
                    }

                    mapping[column.Index] = column with { Header = mapping[column.Index].Header };
                }

                plan = SpreadsheetImporter.Build(draft, plan.Source, plan.Table, mapping);
            }

            var rows = plan.Rows
                .Select(r => new StudioImportRowReport(
                    StudioCommand.Count(r.Line),
                    r.InternalName ?? $"row {StudioCommand.Count(r.Line)}",
                    r.InternalName,
                    CliOutput.Camel(r.Action.ToString()),
                    r.SelectedByDefault,
                    r.Variant?.BaseCharacterId,
                    null,
                    r.ChangedFields.Count > 0 ? string.Join(", ", r.ChangedFields) : null,
                    null,
                    r.Notes))
                .ToList();

            var outcome = parse.GetValue(dryRun)
                ? null
                : await importer.ApplyAsync(draft, plan, null, StudioCommand.Now(provider), cancellationToken).ConfigureAwait(false);

            if (outcome is { Changed: true })
            {
                await StudioCommand.SaveAsync(provider, outcome.Draft, cancellationToken).ConfigureAwait(false);
            }

            if (WriteJson(parse, draft, plan.Source, outcome, rows, [.. plan.Diagnostics.Select(d => d.Message)]))
            {
                return 0;
            }

            Console.Out.WriteLine("Columns:");
            foreach (var column in plan.Columns)
            {
                var use = column.Field == SpreadsheetField.Attribute ? $"attribute {column.AttributeId}" : CliOutput.Camel(column.Field.ToString());
                Console.Out.WriteLine($"  {StudioCommand.Count(column.Index + 1)}. {column.Header}  →  {use}");
            }

            Console.Out.WriteLine();

            foreach (var row in rows)
            {
                Console.Out.WriteLine($"{(row.Included ? "[x]" : "[ ]")} line {row.Key}: {row.Action,-9} {row.Name}{(row.Reason is null ? string.Empty : $"  ({row.Reason})")}");

                foreach (var note in row.Notes)
                {
                    Console.Out.WriteLine($"      {note}");
                }
            }

            foreach (var diagnostic in plan.Diagnostics)
            {
                Console.Out.WriteLine(diagnostic.Message);
            }

            WriteOutcome(outcome);
            return 0;
        });

        return command;
    }

    private static SpreadsheetColumn? ParseColumn(string text, int columnCount, PackDraft draft)
    {
        var parts = text.Split('=', 2, StringSplitOptions.TrimEntries);

        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < 1
            || number > columnCount)
        {
            return null;
        }

        var index = number - 1;
        var field = parts[1];

        if (string.Equals(field, "base", StringComparison.OrdinalIgnoreCase))
        {
            return new SpreadsheetColumn(index, string.Empty, SpreadsheetField.BaseCharacter);
        }

        if (string.Equals(field, "default", StringComparison.OrdinalIgnoreCase))
        {
            return new SpreadsheetColumn(index, string.Empty, SpreadsheetField.IsDefault);
        }

        if (string.Equals(field, "name", StringComparison.OrdinalIgnoreCase))
        {
            return new SpreadsheetColumn(index, string.Empty, SpreadsheetField.DisplayName);
        }

        if (Enum.TryParse<SpreadsheetField>(field, ignoreCase: true, out var known) && known != SpreadsheetField.Attribute)
        {
            return new SpreadsheetColumn(index, string.Empty, known);
        }

        var attribute = (draft.Game.Attributes ?? new Dictionary<string, Xxsm.Packs.Model.AttributeDefinition>())
            .Keys
            .FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));

        return attribute is null ? null : new SpreadsheetColumn(index, string.Empty, SpreadsheetField.Attribute, attribute);
    }

    private static List<CharacterImportChoice> AllChoices(CharacterImportPlan plan) =>
    [
        .. plan.Rows
            .Where(r => r.Action is HashImportAction.Create or HashImportAction.AddHashes)
            .Select(r => new CharacterImportChoice { SourcePath = r.SourcePath, Include = true }),
    ];

    private static async Task<int> FinishCharacterImportAsync(
        ServiceProvider provider,
        ParseResult parse,
        PackDraft draft,
        CharacterImportPlan plan,
        IReadOnlyList<CharacterImportChoice>? choices,
        Func<IReadOnlyList<CharacterImportChoice>?, StudioImportOutcome> apply,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var rows = plan.Rows
            .Select(r => new StudioImportRowReport(
                r.SourcePath,
                r.FolderName,
                r.InternalName,
                CliOutput.Camel(r.Action.ToString()),
                choices?.FirstOrDefault(c => c.SourcePath == r.SourcePath)?.Include ?? r.SelectedByDefault,
                r.Link.BaseCharacterId,
                r.Link.IsSkin ? CliOutput.Camel(r.Link.Confidence.ToString()) : null,
                r.Link.IsSkin ? r.Link.Reason : null,
                r.Action == HashImportAction.AddHashes ? r.NewHashCount : r.Hashes.Count,
                [.. new[] { r.Note }.OfType<string>(), .. r.Diagnostics.Select(d => d.Message)]))
            .ToList();

        var outcome = dryRun ? null : apply(choices);

        if (outcome is { Changed: true })
        {
            await StudioCommand.SaveAsync(provider, outcome.Draft, cancellationToken).ConfigureAwait(false);
        }

        if (WriteJson(parse, draft, plan.Source, outcome, rows, [.. plan.Diagnostics.Select(d => d.Message)]))
        {
            return 0;
        }

        foreach (var row in rows)
        {
            var link = row.SkinOf is null ? string.Empty : $"  outfit of {row.SkinOf} ({row.Confidence})";
            Console.Out.WriteLine(
                $"{(row.Included ? "[x]" : "[ ]")} {row.Action,-9} {row.InternalName}  " +
                $"{EnglishCount.Plural(row.Hashes ?? 0, "hash", "hashes")}{link}");

            foreach (var note in row.Notes)
            {
                Console.Out.WriteLine($"      {note}");
            }
        }

        foreach (var diagnostic in plan.Diagnostics)
        {
            Console.Out.WriteLine(diagnostic.Message);
        }

        var doubtful = plan.Rows.Where(r => r.Link.IsSkin && r.Link.Confidence != LinkConfidence.High).ToList();

        if (doubtful.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Check these outfit links — each is a guess from the names:");

            foreach (var row in doubtful)
            {
                Console.Out.WriteLine($"  {row.InternalName} as an outfit of {row.Link.BaseCharacterId}: {row.Link.Reason}");
            }

            Console.Out.WriteLine($"Undo a wrong one with: xxsm studio character link {draft.GameId} <character> --base");
        }

        WriteOutcome(outcome);
        return 0;
    }

    private static bool WriteJson(
        ParseResult parse,
        PackDraft draft,
        string source,
        StudioImportOutcome? outcome,
        List<StudioImportRowReport> rows,
        List<string> diagnostics)
    {
        if (!parse.GetValue(GlobalOptions.Json))
        {
            return false;
        }

        Console.Out.WriteLine(CliJson.Serialize(new StudioImportReport(
            draft.GameId,
            source,
            outcome is not null,
            outcome?.Created ?? [],
            outcome?.Updated ?? [],
            outcome?.Skipped ?? [],
            rows,
            diagnostics)));

        return true;
    }

    /// <summary>Says what an import did, or that a dry run did nothing.</summary>
    internal static void WriteOutcome(StudioImportOutcome? outcome)
    {
        Console.Out.WriteLine();

        if (outcome is null)
        {
            Console.Out.WriteLine("Dry run: the draft was not changed.");
            return;
        }

        Console.Out.WriteLine(
            $"Created {EnglishCount.Plural(outcome.Created.Count, "character", "characters")}, " +
            $"updated {EnglishCount.Plural(outcome.Updated.Count, "character", "characters")}.");

        foreach (var skipped in outcome.Skipped)
        {
            Console.Out.WriteLine($"  Skipped: {skipped}");
        }
    }
}
