using System.Globalization;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Cli.Output;

/// <summary>Turns a <see cref="ModsInventory"/> into the CLI's report and prints it, for list and scan alike.</summary>
internal static class ModsReportBuilder
{
    /// <summary>Builds the report.</summary>
    /// <param name="inventory">What was found on disk.</param>
    /// <param name="knownVariants">The variants the pack knows, to mark unknown folders; null counts every folder
    /// as known.</param>
    public static ModsInventoryReport Build(
        ModsInventory inventory, IReadOnlySet<string>? knownVariants)
    {
        var folders = inventory.VariantFolders
            .Select(folder => new ModsFolderReport(
                folder.Name,
                knownVariants is null || knownVariants.Contains(folder.Name),
                folder.Mods.Count,
                folder.EnabledCount,
                [.. folder.Mods.Select(ToReport)]))
            .ToList();

        return new ModsInventoryReport(
            inventory.ModsDirectory,
            folders.Count,
            inventory.ModCount,
            inventory.EnabledModCount,
            inventory.UnfiledMods.Count,
            folders.Count(folder => !folder.KnownToPack),
            folders,
            [.. inventory.UnfiledMods.Select(ToReport)],
            [
                .. inventory.Diagnostics.Select(diagnostic => new ScanDiagnostic(
                    diagnostic.Severity.ToString().ToLowerInvariant(),
                    diagnostic.Code,
                    diagnostic.Message,
                    null,
                    null,
                    diagnostic.Paths)),
            ]);
    }

    /// <summary>Prints the report as a human-readable table.</summary>
    public static void WriteHuman(ModsInventoryReport report)
    {
        CliOutput.WriteRows(
        [
            ("Mods folder", PathDisplay.Show(report.ModsDirectory)),
            ("Characters", report.VariantFolderCount.ToString(CultureInfo.InvariantCulture)),
            ("Mods", $"{report.ModCount.ToString(CultureInfo.InvariantCulture)} " +
                     $"({report.EnabledModCount.ToString(CultureInfo.InvariantCulture)} enabled)"),
            ("Not filed yet", report.UnfiledModCount.ToString(CultureInfo.InvariantCulture)),
            ("Unknown characters", report.UnknownFolderCount.ToString(CultureInfo.InvariantCulture)),
        ]);

        foreach (var folder in report.Folders.Where(f => f.ModCount > 0))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"{folder.Name}{(folder.KnownToPack ? string.Empty : "  [not in the pack]")}");

            foreach (var mod in folder.Mods)
            {
                WriteMod(mod);
            }
        }

        if (report.UnfiledMods.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Not filed under any character");

            foreach (var mod in report.UnfiledMods)
            {
                WriteMod(mod);
            }
        }

        // Everything, not only problems: the terminal shows what the app offers to fix.
        if (report.Diagnostics.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Found in the Mods folder");

            foreach (var diagnostic in report.Diagnostics)
            {
                Console.Out.WriteLine($"  [{diagnostic.Severity}] {diagnostic.Message}");

                foreach (var path in diagnostic.Paths ?? [])
                {
                    Console.Out.WriteLine($"      {PathDisplay.Show(path)}");
                }
            }
        }
    }

    private static void WriteMod(ModReport mod)
    {
        var badges = new List<string>();

        if (mod.VariantOverride is { Length: > 0 })
        {
            badges.Add("filed by hand");
        }

        if (mod.SortedBy is { Length: > 0 } sorted)
        {
            // A mod in Others was looked at and not recognised, which is not "unsorted".
            badges.Add(string.Equals(sorted, "unsorted", StringComparison.Ordinal)
                ? "not identified"
                : $"sorted by {sorted}");
        }

        if (mod.ConfigError is { Length: > 0 })
        {
            badges.Add("details unreadable");
        }

        var suffix = badges.Count == 0 ? string.Empty : $"  [{string.Join(", ", badges)}]";

        Console.Out.WriteLine($"  {(mod.Enabled ? "on " : "off")}  {mod.DisplayName,-40}{suffix}");
    }

    private static ModReport ToReport(InstalledMod mod) => new(
        mod.Name,
        mod.DisplayName,
        mod.FolderName,
        mod.Path,
        mod.IsEnabled,
        mod.VariantOverride,
        mod.Config?.Author,
        mod.Config?.ModUrl,
        mod.Config?.SortResult?.DecidedBy,
        mod.ConfigError);
}
