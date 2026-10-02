using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Text;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Loading;
using Xxsm.Packs.Registry;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm pack</c> — install and inspect Game Packs.</summary>
internal static class PackCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("pack", "Install and inspect Game Packs.");
        command.Subcommands.Add(PackRegistryCommands.CreateList());
        command.Subcommands.Add(PackRegistryCommands.CreateInstall());
        command.Subcommands.Add(PackRegistryCommands.CreateUpdate());
        command.Subcommands.Add(PackRegistryCommands.CreatePin());
        command.Subcommands.Add(PackRegistryCommands.CreateAutoUpdate());
        command.Subcommands.Add(PackRegistryCommands.CreateSkipped());
        command.Subcommands.Add(PackRegistryCommands.CreateChanges());
        command.Subcommands.Add(CreateImport());
        command.Subcommands.Add(CreateInstalled());
        command.Subcommands.Add(PackInstalledCommands.CreateUse());
        command.Subcommands.Add(PackInstalledCommands.CreateRemove());
        command.Subcommands.Add(PackInstalledCommands.CreateRestore());
        command.Subcommands.Add(PackInstalledCommands.CreateKeep());
        command.Subcommands.Add(PackInstalledCommands.CreatePrune());
        return command;
    }

    private static Command CreateImport()
    {
        var source = new Argument<string>("path")
        {
            Description = "A pack directory, or a .zip containing one.",
        };

        var overwrite = new Option<bool>("--overwrite")
        {
            Description = "Replace an already-installed pack of the same version.",
        };

        var command = new Command("import", "Install a Game Pack from a local folder or archive.")
        {
            source,
            overwrite,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            // Through the pack service, not the installer: a pack from a file is an update like any other.
            var operation = await provider.GetRequiredService<IPackService>()
                .ImportAsync(Path.GetFullPath(parse.GetValue(source)!), parse.GetValue(overwrite), cancellationToken)
                .ConfigureAwait(false);
            var result = operation.Installed!;

            var report = new PackImportReport(
                result.GameId,
                result.PackVersion,
                result.Directory,
                result.VariantCount,
                result.HashEntryCount,
                [.. result.Diagnostics.Select(Map)],
                operation.SkippedUpdates.Count);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("Installed", $"{result.GameId} {result.PackVersion}"),
                ("Location", PathDisplay.Show(result.Directory)),
                ("Variants", EnglishCount.Plural(result.VariantCount, "variant", "variants")),
                ("Hashes", EnglishCount.Plural(result.HashEntryCount, "entry", "entries")),
            ]);

            WriteDiagnosticSummary(result.Diagnostics);

            PackRegistryCommands.WriteAfterInstall(operation);

            return 0;
        });

        return command;
    }

    private static Command CreateInstalled()
    {
        var command = new Command(
            "installed",
            "List the pack versions on this machine, without consulting a registry.");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var installer = provider.GetRequiredService<IPackInstaller>();

            var installed = await installer.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
            var preferences = await provider.GetRequiredService<IPackPreferencesStore>()
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);

            // Newest first; the one in use is the pinned version when installed, else the newest.
            var active = installed
                .GroupBy(p => p.GameId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => ActivePackVersion.Choose([.. g.Select(p => p.PackVersion)], preferences.ForGame(g.Key).PinnedVersion),
                    StringComparer.OrdinalIgnoreCase);

            var packs = provider.GetRequiredService<IPackService>();
            var entries = new List<PackListEntry>(installed.Count);

            foreach (var p in installed)
            {
                var inUse = string.Equals(active[p.GameId], p.PackVersion, StringComparison.OrdinalIgnoreCase);
                var contents = inUse ? await packs.ReadContentsAsync(p.GameId, cancellationToken).ConfigureAwait(false) : null;
                entries.Add(new PackListEntry(p.GameId, p.PackVersion, p.Directory, inUse, contents));
            }

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new PackListReport(entries));
            }

            if (entries.Count == 0)
            {
                Console.Out.WriteLine("No Game Packs installed yet.");
                Console.Out.WriteLine("Install one with: xxsm pack import <folder-or-zip>");
                return 0;
            }

            CliOutput.WriteRows(
            [
                .. entries.Select(e => (
                    $"{e.GameId} {e.PackVersion}",
                    e.IsActive ? $"{PathDisplay.Show(e.Directory)}  (in use){Describe(e.Contents)}" : PathDisplay.Show(e.Directory))),
            ]);

            return 0;
        });

        return command;
    }

    /// <summary>" 124 characters and 28 skins, 19 still waiting for hashes.", in the window's words.</summary>
    private static string Describe(Xxsm.Packs.Loading.PackContents? contents)
    {
        if (contents is null)
        {
            return string.Empty;
        }

        var line = $"{contents.Characters} character{(contents.Characters == 1 ? "" : "s")}";

        if (contents.Skins > 0)
        {
            line += $" and {contents.Skins} skin{(contents.Skins == 1 ? "" : "s")}";
        }

        if (contents.WaitingForHashes > 0)
        {
            line += $", {contents.WaitingForHashes} still waiting for hashes";
        }

        return "  " + line + ".";
    }

    internal static ScanDiagnostic Map(PackDiagnostic diagnostic) => new(
        diagnostic.Severity.ToString().ToLowerInvariant(),
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.File,
        diagnostic.Subject);

    internal static void WriteDiagnosticSummary(IReadOnlyList<PackDiagnostic> diagnostics)
    {
        var errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (errors == 0 && warnings == 0)
        {
            return;
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(
            $"{EnglishCount.Plural(errors, "error", "errors")}, " +
            $"{EnglishCount.Plural(warnings, "warning", "warnings")}:");

        foreach (var diagnostic in diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).Take(20))
        {
            Console.Out.WriteLine($"  {diagnostic}");
        }

        if (errors + warnings > 20)
        {
            Console.Out.WriteLine($"  … and {(errors + warnings - 20).ToString(
                System.Globalization.CultureInfo.InvariantCulture)} more. Run with --json for all of them.");
        }
    }
}
