using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Io;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Registry;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm pack list</c>, <c>install</c>, <c>update</c> and <c>pin</c>: the registry side of packs.</summary>
internal static class PackRegistryCommands
{
    /// <summary>The registry to use instead of the configured ones: a URL, a folder or an <c>index.json</c>.</summary>
    private static Option<string[]> RegistryOption() => new("--registry")
    {
        Description =
            "Registry to use instead of the configured ones: a URL, or a path to a folder " +
            "or index.json. May be given more than once; later ones win on ties.",
        AllowMultipleArgumentsPerToken = false,
    };

    /// <summary>Builds <c>xxsm pack list</c>.</summary>
    public static Command CreateList()
    {
        var registry = RegistryOption();

        var installedOnly = new Option<bool>("--installed")
        {
            Description = "Only what is on this machine. Never touches the network.",
        };

        var command = new Command("list", "List Game Packs on offer and installed.")
        {
            registry,
            installedOnly,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var packs = provider.GetRequiredService<IPackService>();

            var sources = parse.GetValue(installedOnly)
                ? []
                : Sources(parse.GetValue(registry));

            var catalog = await packs
                .GetCatalogAsync(parse.GetValue(installedOnly) ? [] : sources, cancellationToken)
                .ConfigureAwait(false);

            var report = new PackCatalogReport(
                [.. catalog.Entries.Select(Map)],
                catalog.Failures,
                catalog.AutoUpdate);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteCatalog(catalog);

            // Being offline is not a failure: listing what is installed worked.
            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack install</c>.</summary>
    public static Command CreateInstall()
    {
        var game = new Argument<string>("game")
        {
            Description = "The game id, as xxsm pack list reports it.",
        };

        var version = new Option<string?>("--version")
        {
            Description = "The pack version to install. Defaults to the newest usable one.",
        };

        var overwrite = new Option<bool>("--overwrite")
        {
            Description = "Reinstall a version that is already on disk.",
        };

        var registry = RegistryOption();

        var command = new Command("install", "Download, verify and install a Game Pack.")
        {
            game,
            version,
            overwrite,
            registry,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var packs = provider.GetRequiredService<IPackService>();

            var result = await packs
                .InstallAsync(
                    parse.GetValue(game)!,
                    parse.GetValue(version),
                    Sources(parse.GetValue(registry)),
                    parse.GetValue(overwrite),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(Map(result));
            }

            WriteOperation(result);
            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack update</c>.</summary>
    public static Command CreateUpdate()
    {
        var game = new Option<string?>("--game")
        {
            Description = "Update one game. Without it, every game with a pack installed.",
        };

        var registry = RegistryOption();

        var command = new Command("update", "Update installed Game Packs to the newest usable version.")
        {
            game,
            registry,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var packs = provider.GetRequiredService<IPackService>();

            var results = await packs
                .UpdateAsync(parse.GetValue(game), Sources(parse.GetValue(registry)), cancellationToken)
                .ConfigureAwait(false);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new PackUpdateReport([.. results.Select(Map)]));
            }

            if (results.Count == 0)
            {
                Console.Out.WriteLine("No Game Packs are installed, so there is nothing to update.");
                return 0;
            }

            foreach (var result in results)
            {
                WriteOperation(result);
            }

            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack pin</c>.</summary>
    public static Command CreatePin()
    {
        var game = new Argument<string>("game") { Description = "The game id." };

        var version = new Argument<string?>("version")
        {
            Description = "The pack version to hold this game at. Omit with --clear to unpin.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var clear = new Option<bool>("--clear") { Description = "Remove the pin." };

        var command = new Command("pin", "Hold a game at one pack version, or let it follow the newest.")
        {
            game,
            version,
            clear,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var packs = provider.GetRequiredService<IPackService>();

            var gameId = parse.GetValue(game)!;
            var wanted = parse.GetValue(version);
            var unpin = parse.GetValue(clear);

            PackGamePreference? preference = null;

            if (unpin || wanted is { Length: > 0 })
            {
                preference = await packs
                    .PinAsync(gameId, unpin ? null : wanted, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (preference is null)
            {
                // Nothing to change: report the current state rather than nothing.
                var catalog = await packs.GetCatalogAsync([], cancellationToken).ConfigureAwait(false);
                preference = catalog.Find(gameId)?.Preference ?? new PackGamePreference();
            }

            var message = preference.PinnedVersion is { Length: > 0 } pinned
                ? $"{gameId} is pinned to {pinned}: XXSM uses it once it is installed, and updates leave it alone."
                : $"{gameId} is not pinned and follows the newest usable pack.";

            var report = new PackPinReport(gameId, preference.PinnedVersion, message);

            Console.Out.WriteLine(parse.GetValue(GlobalOptions.Json)
                ? CliJson.Serialize(report)
                : message);

            return 0;
        });

        return command;
    }

    /// <summary>Null when the user named none, so the configured registries are used.</summary>
    private static string[]? Sources(string[]? registries) =>
        registries is { Length: > 0 } ? registries : null;

    private static void WriteCatalog(PackCatalogResult catalog)
    {
        if (catalog.Entries.Count == 0)
        {
            Console.Out.WriteLine("No Game Packs are installed and none are on offer.");
        }

        foreach (var entry in catalog.Entries)
        {
            var state = entry.ActiveVersion is { Length: > 0 } active
                ? $"installed {active}"
                : "not installed";

            if (entry.Preference.PinnedVersion is { Length: > 0 } pinned)
            {
                state += $", pinned to {pinned}";
            }

            // Held on an older version: say what it is held back from, and how to stop.
            if (entry.NewerThanHeld is { } newer)
            {
                state += $", staying on it though {newer} is newer — xxsm pack pin {entry.GameId} --clear follows updates";
            }

            if (entry.UpdateAvailable && entry.TargetVersion is { } target)
            {
                state += $", {target.PackVersion} available";
            }

            Console.Out.WriteLine($"{entry.GameId}  —  {entry.DisplayName}  ({state})");

            if (entry.Registry is { Length: > 0 } registry)
            {
                Console.Out.WriteLine($"    from {registry}");
            }

            foreach (var version in entry.Versions)
            {
                var marks = new List<string>();

                if (entry.InstalledVersions.Contains(version.PackVersion, StringComparer.OrdinalIgnoreCase))
                {
                    marks.Add(string.Equals(version.PackVersion, entry.ActiveVersion, StringComparison.OrdinalIgnoreCase)
                        ? "active"
                        : "installed");
                }

                if (!version.IsInstallable)
                {
                    marks.Add("cannot be used");
                }

                var suffix = marks.Count > 0 ? $"  [{string.Join(", ", marks)}]" : string.Empty;

                Console.Out.WriteLine($"    {version.PackVersion}{suffix}");

                if (version.RefusalReason is { Length: > 0 } reason)
                {
                    Console.Out.WriteLine($"        {reason}");
                }
            }

            Console.Out.WriteLine();
        }

        if (catalog.Failures.Count == 0)
        {
            return;
        }

        Console.Out.WriteLine("Could not reach:");

        foreach (var failure in catalog.Failures)
        {
            Console.Out.WriteLine($"  {failure.Registry}");
            Console.Out.WriteLine($"      {failure.Message}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine("Installed packs still work. This only affects downloading new ones.");
    }

    /// <summary>Builds <c>xxsm pack auto-update</c>; with no argument it says how it is set.</summary>
    public static Command CreateAutoUpdate()
    {
        var state = new Argument<string?>("state")
        {
            Description = "on or off. Omit to see how it is set.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        state.AcceptOnlyFromAmong("on", "off");

        var command = new Command(
            "auto-update",
            "Whether XXSM installs a newer pack by itself when it finds one (on), or says so and waits (off).")
        {
            state,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var packs = provider.GetRequiredService<IPackService>();

            bool enabled;

            if (parse.GetValue(state) is { } wanted)
            {
                enabled = (await packs.SetAutoUpdateAsync(wanted == "on", cancellationToken).ConfigureAwait(false)).AutoUpdate;
            }
            else
            {
                enabled = (await packs.GetCatalogAsync([], cancellationToken).ConfigureAwait(false)).AutoUpdate;
            }

            var message = enabled
                ? "Game packs update by themselves: when XXSM starts, and once a day while it is open, it installs any newer pack. A pinned game is left alone."
                : "Game packs do not update by themselves: XXSM says when a newer pack is ready, and waits for Update.";

            Console.Out.WriteLine(parse.GetValue(GlobalOptions.Json)
                ? CliJson.Serialize(new PackAutoUpdateReport(enabled, message))
                : message);

            return 0;
        });

        return command;
    }

    /// <summary>Builds <c>xxsm pack changes</c>: what the last update changed, read back.</summary>
    public static Command CreateChanges()
    {
        var game = new Argument<string>("game") { Description = "The game id." };

        var command = new Command("changes", "Show what the last pack update changed: What's new.")
        {
            game,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var store = provider.GetRequiredService<IPackChangesStore>();
            var gameId = parse.GetValue(game)!;

            var changes = await store.ReadAsync(gameId, cancellationToken).ConfigureAwait(false);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(new PackChangesReport(gameId, changes));
            }

            if (changes is null)
            {
                Console.Out.WriteLine($"No update of {gameId}'s pack has been installed since it was first installed.");
                return 0;
            }

            WriteChanges(changes);
            return 0;
        });

        return command;
    }

    /// <summary>What's new, in the words the window uses.</summary>
    internal static void WriteChanges(PackChanges changes)
    {
        Console.Out.WriteLine($"What's new in {changes.GameId} {changes.ToVersion} (from {changes.FromVersion})");

        if (changes.IsEmpty)
        {
            Console.Out.WriteLine("  No characters, outfits, names, hashes or portraits changed.");
            return;
        }

        Section("New characters", changes.Added);
        Section("New outfits", changes.AddedOutfits);
        Section("Removed", changes.Removed);
        Section("Renamed", [.. changes.Renamed.Select(r => $"{r.From} → {r.To}")]);
        Section("Hashes for the first time", changes.FirstHashes);
        Section("Hashes changed", [.. changes.HashesChanged.Select(Describe)]);
        Section("New portraits", changes.NewPortraits);
        Section("Changed portraits", changes.ChangedPortraits);

        static string Describe(PackHashChange change) =>
            $"{change.Name} ({string.Join(", ", new[] { change.AddedCount > 0 ? $"{change.AddedCount} added" : null, change.RemovedCount > 0 ? $"{change.RemovedCount} removed" : null }.OfType<string>())})";

        static void Section(string heading, IReadOnlyList<string> names)
        {
            if (names.Count > 0)
            {
                Console.Out.WriteLine($"  {heading} ({names.Count}): {string.Join(", ", names)}");
            }
        }
    }

    /// <summary>Builds <c>xxsm pack skipped</c>: the review of what the last update withheld, read back.</summary>
    public static Command CreateSkipped()
    {
        var game = new Argument<string>("game") { Description = "The game id." };

        var forget = new Option<bool>("--forget")
        {
            Description = "Throw the list away. Nothing about the characters themselves changes.",
        };

        var command = new Command(
            "skipped",
            "Show what the last pack update withheld from characters you have edited. " +
            "Take one with: xxsm character edit <name> --reset <field>.")
        {
            game,
            forget,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var store = provider.GetRequiredService<ISkippedUpdatesStore>();
            var gameId = parse.GetValue(game)!;

            if (parse.GetValue(forget))
            {
                var forgotten = await store.ClearAsync(gameId, cancellationToken).ConfigureAwait(false);

                Console.Out.WriteLine(forgotten
                    ? $"Forgot what the last update withheld from {gameId}."
                    : $"There was nothing recorded for {gameId}.");

                return 0;
            }

            var record = await store.ReadAsync(gameId, cancellationToken).ConfigureAwait(false);

            var report = new SkippedUpdatesRecordReport(
                gameId,
                record?.PackVersion,
                record?.RecordedAt,
                record is null ? [] : [.. record.Updates.Select(Map)]);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            if (record is null)
            {
                Console.Out.WriteLine($"The last pack update withheld nothing from {gameId}.");
                return 0;
            }

            Console.Out.WriteLine(
                $"{gameId}: {record.Updates.Count} character(s) kept your version when " +
                $"{record.PackVersion ?? "the pack"} was installed.");

            foreach (var skipped in record.Updates)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"  {skipped.DisplayName} ({skipped.InternalName})");

                foreach (var change in skipped.Changes)
                {
                    Console.Out.WriteLine(
                        $"      {change.Field}: the pack says {Quote(change.PackNew)}, " +
                        $"yours stays {Quote(change.UserValue)}");
                }
            }

            return 0;
        });

        return command;
    }

    private static void WriteOperation(PackOperationResult result)
    {
        Console.Out.WriteLine(result.Message);

        if (result.Directory is { Length: > 0 } directory)
        {
            Console.Out.WriteLine($"    {PathDisplay.Show(directory)}");
        }

        WriteAfterInstall(result);
    }

    /// <summary>What an install changed, withheld from edited characters, and caught up with.</summary>
    internal static void WriteAfterInstall(PackOperationResult result)
    {
        if (result.Changes is { } changes)
        {
            Console.Out.WriteLine();
            WriteChanges(changes);
        }

        if (result.SkippedUpdates.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Skipped updates");
            Console.Out.WriteLine(
                "  You have edited these, so the pack's changes were not applied. " +
                "Your versions are untouched.");

            foreach (var skipped in result.SkippedUpdates)
            {
                Console.Out.WriteLine($"  {skipped.InternalName}");

                foreach (var change in skipped.Changes)
                {
                    // Both values: what the user turned down, and what they are keeping.
                    Console.Out.WriteLine(
                        $"      {change.Field}: the pack now says {Quote(change.PackNew)}, " +
                        $"yours stays {Quote(change.UserValue)}");
                }
            }
        }

        if (result.AdoptionCandidates.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("The pack has caught up with characters you created:");

            foreach (var candidate in result.AdoptionCandidates)
            {
                Console.Out.WriteLine($"  {candidate}");
            }
        }
    }

    private static PackCatalogEntryReport Map(PackCatalogEntry entry) => new(
        entry.GameId,
        entry.DisplayName,
        entry.Registry,
        entry.ActiveVersion,
        entry.Preference.PinnedVersion,
        entry.NewerThanHeld,
        entry.UpdateAvailable,
        [.. entry.Versions.Select(version => new PackCatalogVersionReport(
            version.PackVersion,
            version.Version.PackSchemaVersion,
            CliOutput.Camel(version.Availability.ToString()),
            version.RefusalReason,
            entry.InstalledVersions.Contains(version.PackVersion, StringComparer.OrdinalIgnoreCase),
            string.Equals(version.PackVersion, entry.ActiveVersion, StringComparison.OrdinalIgnoreCase),
            version.Version.SizeBytes,
            version.Version.Changelog))]);

    private static PackOperationReport Map(PackOperationResult result) => new(
        result.GameId,
        CliOutput.Camel(result.Outcome.ToString()),
        result.PackVersion,
        result.PreviousVersion,
        result.Directory,
        result.Message,
        [.. result.SkippedUpdates.Select(Map)],
        result.AdoptionCandidates,
        result.Changes);

    private static SkippedUpdateReport Map(SkippedUpdate skipped) => new(
        skipped.InternalName,
        skipped.DisplayName,
        [.. skipped.Changes.Select(change =>
            new SkippedFieldReport(change.Field, change.PackNew, change.UserValue))]);

    /// <summary>Quotes a value, or says it is unset, so an empty line is never ambiguous.</summary>
    private static string Quote(string? value) =>
        value is { Length: > 0 } ? $"\"{value}\"" : "nothing";
}
