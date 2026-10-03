using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod changed</c> and <c>xxsm mod revert</c>: INIs XXSM changed, and putting the authors' back.</summary>
internal static class ModRevertCommands
{
    /// <summary>Builds both commands.</summary>
    public static IEnumerable<Command> Create() => [CreateChanged(), CreateRevert()];

    private static Command CreateChanged()
    {
        var folder = new Argument<string?>("folder")
        {
            Description = "One mod folder. Without it, every mod in the Mods folder.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var command = new Command(
            "changed",
            "List the mods whose INIs differ from the copies XXSM kept before first changing them: the keys and defaults " +
            "that differ, beside the authors' values.")
        {
            folder,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var originals = provider.GetRequiredService<IIniOriginalsService>();
            var changes = await ReadAsync(provider, originals, parse, folder, mods, game, cancellationToken).ConfigureAwait(false);

            return Write(parse, new ModIniChangesListReport(null, false, [.. changes.Select(Report)], []));
        });

        return command;
    }

    private static Command CreateRevert()
    {
        var folder = new Argument<string?>("folder")
        {
            Description = "The mod folder. Give it, or --all.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var all = new Option<bool>("--all")
        {
            Description = "Every mod in the Mods folder with an INI XXSM changed.",
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var keys = new Option<bool>("--keys")
        {
            Description = "Put back only the key bindings.",
        };

        var defaults = new Option<bool>("--defaults")
        {
            Description = "Put back only the saved settings' defaults.",
        };

        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Say what would be put back and change nothing.",
        };

        var command = new Command(
            "revert",
            "Put back the authors' INIs from the copies XXSM kept before first changing them: the whole file, or only " +
            "the keys or the defaults. A copy of each INI as it is now goes to the trash first.")
        {
            folder,
            all,
            mods,
            game,
            keys,
            defaults,
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            if (parse.GetValue(keys) && parse.GetValue(defaults))
            {
                throw new ModOperationException("Give --keys or --defaults, not both. Without either, the whole file is put back.");
            }

            if (parse.GetValue(all) == parse.GetValue(folder) is { Length: > 0 })
            {
                throw new ModOperationException("Give one mod folder, or --all for every mod in the Mods folder.");
            }

            var scope = parse.GetValue(keys)
                ? IniRevertScope.Keys
                : parse.GetValue(defaults) ? IniRevertScope.Defaults : IniRevertScope.Everything;

            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var originals = provider.GetRequiredService<IIniOriginalsService>();
            var changes = await ReadAsync(provider, originals, parse, folder, mods, game, cancellationToken).ConfigureAwait(false);
            var trashed = new List<string>();
            var reverting = !parse.GetValue(dryRun);

            if (reverting)
            {
                foreach (var mod in changes)
                {
                    var rewrite = await originals.RevertAsync(mod.ModFolder, scope, cancellationToken).ConfigureAwait(false);
                    trashed.AddRange(rewrite.Trashed.Select(item => item.TrashedPath));
                }
            }

            var report = new ModIniChangesListReport(
                CliOutput.Camel(scope.ToString()),
                reverting,
                [.. changes.Select(mod => Report(mod, scope))],
                trashed);

            return Write(parse, report);
        });

        return command;
    }

    /// <summary>The one mod given, or every mod with changes in the Mods folder.</summary>
    private static async Task<List<ModIniChanges>> ReadAsync(
        ServiceProvider provider,
        IIniOriginalsService originals,
        ParseResult parse,
        Argument<string?> folder,
        Option<string?> mods,
        Option<string?> game,
        CancellationToken cancellationToken)
    {
        if (parse.GetValue(folder) is { Length: > 0 } one)
        {
            var read = await originals.ReadAsync(Path.GetFullPath(one), cancellationToken).ConfigureAwait(false);
            return [read];
        }

        var modsDirectory = await ModsFolderOptions.ResolveAsync(provider, parse, mods, game, cancellationToken).ConfigureAwait(false);
        var found = await originals.FindAsync(modsDirectory, cancellationToken).ConfigureAwait(false);

        return [.. found.Select(changed => changed.Changes)];
    }

    private static ModIniChangesReport Report(ModIniChanges changes) => Report(changes, IniRevertScope.Everything);

    /// <summary>A mod's changed lines, only those the scope puts back.</summary>
    private static ModIniChangesReport Report(ModIniChanges changes, IniRevertScope scope) => new(
        changes.ModFolder,
        [
            .. changes.Files.SelectMany(file => file.Changes
                .Where(change => scope == IniRevertScope.Everything ||
                                 change.Kind == (scope == IniRevertScope.Keys ? IniChangeKind.Key : IniChangeKind.Default))
                .Select(change => new ModIniLineReport(
                    file.File, CliOutput.Camel(change.Kind.ToString()), change.Section, change.Name, change.Original, change.Current))),
        ],
        scope == IniRevertScope.Everything ? [.. changes.Files.Where(file => file.HasOtherChanges).Select(file => file.File)] : [],
        changes.Problems);

    private static int Write(ParseResult parse, ModIniChangesListReport report)
    {
        if (parse.GetValue(GlobalOptions.Json))
        {
            return CliJson.Write(report);
        }

        if (report.Mods.All(mod => mod.Lines.Count == 0 && mod.OtherChanges.Count == 0))
        {
            Console.Out.WriteLine("No INI differs from the author's.");
        }

        foreach (var mod in report.Mods)
        {
            if (mod.Lines.Count == 0 && mod.OtherChanges.Count == 0 && mod.Problems.Count == 0)
            {
                continue;
            }

            Console.Out.WriteLine(PathDisplay.Show(mod.Mod));

            foreach (var line in mod.Lines)
            {
                Console.Out.WriteLine($"  {line.File} [{line.Section}] {line.Name,-12} {line.Current} (author: {line.Original})");
            }

            foreach (var file in mod.OtherChanges)
            {
                Console.Out.WriteLine($"  {file}: other lines differ too");
            }

            foreach (var problem in mod.Problems)
            {
                CliOutput.WriteError(problem);
            }
        }

        if (report.Scope is not null)
        {
            var count = report.Mods.Count(mod => mod.Lines.Count > 0 || mod.OtherChanges.Count > 0);
            Console.Out.WriteLine(report.Reverted
                ? $"Put back the authors' INIs in {EnglishCount.Plural(count, "mod", "mods")}. A copy of each as it was is in the trash."
                : "Nothing was changed (--dry-run).");
        }

        return 0;
    }
}
