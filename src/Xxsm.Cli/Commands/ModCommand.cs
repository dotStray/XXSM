using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod</c>: what a mod folder says about itself, and what can be done to it.</summary>
internal static class ModCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("mod", "Inspect and change the mods in a Mods folder.")
        {
            CreateSignals(),
            CreatePreview(),
            ModAddCommand.Create(SortCommand.LoadGameDataAsync),
            ModDownloadCommands.Create(),
            ModRandomiseCommand.Create(),
            ModKeysCommand.Create(),
            ModExportCommand.Create(),
            ModImportCommand.Create(),
        };

        foreach (var subcommand in ModGameBananaCommands.Create())
        {
            command.Subcommands.Add(subcommand);
        }

        foreach (var subcommand in ModOperationCommands.Create())
        {
            command.Subcommands.Add(subcommand);
        }

        return command;
    }

    /// <summary><c>xxsm mod preview</c>: which image the detail pane would show for a mod, and why.</summary>
    private static Command CreatePreview()
    {
        var folder = new Argument<string?>("folder")
        {
            Description = "A single mod folder. Omit it to report every mod in a Mods folder.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var mods = ModsFolderOptions.Mods();
        var game = ModsFolderOptions.Game();

        var command = new Command("preview", "Show which image a mod's thumbnail would come from.")
        {
            folder,
            mods,
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var previews = provider.GetRequiredService<IModPreviewSource>();
            var configs = provider.GetRequiredService<IModConfigStore>();

            List<ModPreviewReport> rows = [];

            if (parse.GetValue(folder) is { Length: > 0 } single)
            {
                rows.Add(await DescribeAsync(previews, configs, single, single, cancellationToken)
                    .ConfigureAwait(false));
            }
            else
            {
                var directory = await ModsFolderOptions
                    .ResolveAsync(provider, parse, mods, game, cancellationToken).ConfigureAwait(false);

                var inventory = await provider.GetRequiredService<IModRepository>()
                    .ScanAsync(directory, cancellationToken).ConfigureAwait(false);

                foreach (var mod in inventory.AllMods)
                {
                    rows.Add(await DescribeAsync(previews, configs, mod.Path, mod.DisplayName, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(rows);
            }

            WritePreviews(rows);
            return 0;
        });

        return command;
    }

    private static async Task<ModPreviewReport> DescribeAsync(
        IModPreviewSource previews,
        IModConfigStore configs,
        string modFolder,
        string name,
        CancellationToken cancellationToken)
    {
        var config = await configs.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);

        var preview = await previews
            .FindAsync(modFolder, config, bounds: null, cancellationToken).ConfigureAwait(false);

        return new ModPreviewReport(
            name,
            modFolder,
            preview?.RelativePath,
            preview?.Match.ToString().ToLowerInvariant());
    }

    private static void WritePreviews(List<ModPreviewReport> rows)
    {
        var found = rows.Count(row => row.Image is not null);

        CliOutput.WriteRows(
        [
            ("Mods looked at", rows.Count.ToString(CultureInfo.InvariantCulture)),
            ("With a thumbnail", found.ToString(CultureInfo.InvariantCulture)),
            ("Without", (rows.Count - found).ToString(CultureInfo.InvariantCulture)),
        ]);

        Console.Out.WriteLine();

        foreach (var row in rows)
        {
            Console.Out.WriteLine(
                $"  {Truncate(row.Name, 44),-44}  {row.Match ?? "none",-14}  {row.Image ?? "—"}");
        }
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : string.Concat(value.AsSpan(0, width - 1), "…");

    private static Command CreateSignals()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder to scan." };
        var maxDepth = new Option<int>("--max-depth")
        {
            Description = "How many directory levels to descend.",
            DefaultValueFactory = _ => ModScanBounds.Default.MaxDepth,
        };

        var maxFiles = new Option<int>("--max-files")
        {
            Description = "How many files to look at.",
            DefaultValueFactory = _ => ModScanBounds.Default.MaxFiles,
        };

        var command = new Command(
            "signals",
            "Extract the hashes, sections and file names a mod folder carries.")
        {
            folder,
            maxDepth,
            maxFiles,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var bounds = new ModScanBounds(
                parse.GetValue(maxDepth),
                parse.GetValue(maxFiles),
                ModScanBounds.Default.MaxIniBytes);

            var signals = await provider
                .GetRequiredService<IModSignalExtractor>()
                .ExtractAsync(parse.GetValue(folder)!, bounds, cancellationToken)
                .ConfigureAwait(false);

            var report = new ModSignalReport(
                signals.Root,
                signals.FilesSeen,
                signals.IniFiles.Count,
                signals.IniBytesRead,
                signals.IsComplete,
                signals.LimitsReached.ToString(),
                signals.Hashes,
                [
                    .. signals.OverrideSections.Select(section => new ModSignalSection(
                        section.Name,
                        section.Hashes,
                        section.MatchFirstIndex,
                        section.MatchPriority,
                        section.File,
                        section.Line)),
                ],
                signals.AssetNames,
                signals.IniFiles,
                signals.HashJsonFiles,
                [
                    .. signals.Diagnostics.Select(diagnostic => new ScanDiagnostic(
                        diagnostic.Severity.ToString().ToLowerInvariant(),
                        diagnostic.Code,
                        diagnostic.Message,
                        null,
                        diagnostic.Line > 0
                            ? $"line {diagnostic.Line.ToString(CultureInfo.InvariantCulture)}"
                            : null)),
                ]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteHuman(report);
            return 0;
        });

        return command;
    }

    private static void WriteHuman(ModSignalReport report)
    {
        CliOutput.WriteRows(
        [
            ("Mod folder", PathDisplay.Show(report.Root)),
            ("Files seen", report.FilesSeen.ToString(CultureInfo.InvariantCulture)),
            ("INIs read", $"{report.IniFilesRead.ToString(CultureInfo.InvariantCulture)} ({report.IniBytesRead.ToString(CultureInfo.InvariantCulture)} bytes)"),
            ("Shipped hash.json", report.HashJsonFiles.Count == 0
                ? "none"
                : string.Join(", ", report.HashJsonFiles)),
            ("Scan complete", report.Complete ? "yes" : $"no — stopped at {report.LimitsReached}"),
            (string.Empty, string.Empty),
            ("Hashes", EnglishCount.Plural(report.Hashes.Count, "hash", "hashes")),
            ("Override sections", report.OverrideSections.Count.ToString(CultureInfo.InvariantCulture)),
            ("Asset names", report.AssetNames.Count.ToString(CultureInfo.InvariantCulture)),
        ]);

        if (report.Hashes.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Hashes");
            foreach (var hash in report.Hashes)
            {
                var sections = report.OverrideSections
                    .Where(section => section.Hashes.Contains(hash, StringComparer.Ordinal))
                    .Select(section => section.Name)
                    .ToList();

                var source = sections.Count == 0 ? "from hash.json" : string.Join(", ", sections);
                Console.Out.WriteLine($"  {hash,-18} {PathDisplay.Show(source)}");
            }
        }

        if (report.AssetNames.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("File names (the tier-3 fallback)");
            foreach (var name in report.AssetNames)
            {
                Console.Out.WriteLine($"  {name}");
            }
        }

        if (report.Diagnostics.Count == 0)
        {
            return;
        }

        Console.Out.WriteLine();
        foreach (var diagnostic in report.Diagnostics)
        {
            Console.Out.WriteLine($"  [{diagnostic.Severity}] {diagnostic.Message}");
        }
    }
}
