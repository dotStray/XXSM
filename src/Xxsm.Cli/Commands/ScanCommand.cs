using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Text;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm scan</c>: the merged inventory of a game's variants, as the UI sees it.</summary>
/// <remarks>With <c>--mods</c> it also inventories the Mods folder and marks folders the pack does not know.</remarks>
internal static class ScanCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var game = new Option<string>("--game")
        {
            Description = "The game to scan, for example the gameId from a pack's manifest.",
            Required = true,
        };

        var packDirectory = new Option<string?>("--pack")
        {
            Description = "Scan a pack directory directly instead of the installed one.",
        };

        var includeHidden = new Option<bool>("--include-hidden")
        {
            Description = "Include variants the user has hidden.",
        };

        var mods = new Option<string?>("--mods")
        {
            Description = "Also inventory the mods in this Mods folder and match them to the pack.",
        };

        var command = new Command("scan", "List a game's variants, and optionally the mods on disk.")
        {
            game,
            packDirectory,
            includeHidden,
            mods,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var gameId = parse.GetValue(game)!;
            var explicitPack = parse.GetValue(packDirectory);

            var directory = explicitPack ?? await provider
                .GetRequiredService<IPackInstaller>()
                .FindActivePackDirectoryAsync(gameId, cancellationToken)
                .ConfigureAwait(false);

            if (directory is null)
            {
                throw new PackLoadException(
                    $"No Game Pack is installed for '{gameId}'. " +
                    "Install one with: xxsm pack import <folder-or-zip>");
            }

            var data = await provider
                .GetRequiredService<IGameDataService>()
                .LoadAsync(gameId, directory, cancellationToken)
                .ConfigureAwait(false);

            var variants = (parse.GetValue(includeHidden) ? data.Variants : [.. data.VisibleVariants])
                .OrderBy(v => v.InternalName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            ModsInventoryReport? modsReport = null;

            if (parse.GetValue(mods) is { Length: > 0 } modsDirectory)
            {
                var inventory = await provider
                    .GetRequiredService<IModRepository>()
                    .ScanAsync(modsDirectory, cancellationToken)
                    .ConfigureAwait(false);

                modsReport = ModsReportBuilder.Build(
                    inventory,
                    data.Variants.Select(v => v.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            var report = new ScanReport(
                data.GameId,
                data.Game.DisplayName,
                data.Pack?.PackVersion,
                data.Pack?.Directory,
                variants.Count,
                variants.Select(v => v.FamilyId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                variants.Count(v => v.IsSkin),
                data.Variants.Count(v => v.Hidden),
                variants.Count(v => v.IsCustomised),
                variants.Count(v => v.IsHashesPending),
                variants.Sum(v => v.Hashes.Count),
                [.. variants.Select(ToScanVariant)],
                [.. data.Diagnostics.Select(PackCommand.Map)],
                modsReport);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteHuman(report);
            return 0;
        });

        return command;
    }

    private static ScanVariant ToScanVariant(MergedVariant v) => new(
        v.InternalName,
        v.DisplayName,
        v.FamilyId,
        v.BaseCharacterId,
        v.IsDefaultVariant,
        v.Origin.ToString().ToLowerInvariant(),
        v.IsLocked,
        v.Hidden,
        v.Hashes.Count,
        v.IsHashesPending,
        v.ModFilesName,
        v.Image is not null,
        v.Attributes.ToDictionary(
            a => a.Key,
            IReadOnlyList<string> (a) => a.Value.Ids,
            StringComparer.OrdinalIgnoreCase));

    private static void WriteHuman(ScanReport report)
    {
        if (report.Mods is { } mods)
        {
            ModsReportBuilder.WriteHuman(mods);
            Console.Out.WriteLine();
        }

        CliOutput.WriteRows(
        [
            ("Game", $"{report.DisplayName} ({report.GameId})"),
            ("Pack", report.PackVersion is null ? "(none installed)" : report.PackVersion),
            ("Location", report.PackDirectory is { } shown ? PathDisplay.Show(shown) : "-"),
            (string.Empty, string.Empty),
            ("Variants", EnglishCount.Plural(report.VariantCount, "variant", "variants")),
            ("Families", EnglishCount.Plural(report.FamilyCount, "family", "families")),
            ("Alternate outfits", report.SkinCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Awaiting hashes", report.HashesPendingCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Customised", report.CustomisedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Hidden", report.HiddenCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Hash entries", report.HashEntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);

        Console.Out.WriteLine();

        foreach (var variant in report.Variants)
        {
            var family = variant.BaseCharacterId is { Length: > 0 } parent
                ? $"outfit of {parent}"
                : "base";

            var hashes = variant.HashesPending
                ? "no hashes yet"
                : EnglishCount.Plural(variant.HashCount, "hash", "hashes");

            var badges = new List<string>();
            if (variant.Origin != "pack")
            {
                badges.Add("custom");
            }

            if (variant.Hidden)
            {
                badges.Add("hidden");
            }

            var suffix = badges.Count == 0 ? string.Empty : $"  [{string.Join(", ", badges)}]";

            Console.Out.WriteLine(
                $"  {variant.InternalName,-32} {variant.DisplayName,-28} {family,-22} {hashes}{suffix}");
        }

        var errors = report.Diagnostics.Count(d => d.Severity == "error");
        var warnings = report.Diagnostics.Count(d => d.Severity == "warning");

        if (errors > 0 || warnings > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"{EnglishCount.Plural(errors, "error", "errors")}, " +
                $"{EnglishCount.Plural(warnings, "warning", "warnings")}. Run with --json to see them all.");
        }
    }
}
