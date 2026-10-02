using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Io;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm text</c>: the wording the desktop app shows, from <c>text.reference.json</c>.</summary>
internal static class TextCommand
{
    /// <summary>The exit code for a file that will not do what its author meant.</summary>
    private const int ProblemExitCode = 1;

    /// <summary>Builds the command.</summary>
    public static Command Create() =>
        new("text", "Change the words the desktop app shows, without rebuilding it.")
        {
            CreateShow(),
            CreateInit(),
            CreateCheck(),
        };

    private static Command CreateShow()
    {
        var command = new Command("show", "Where the wording files are and what they change.");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var store = provider.GetRequiredService<ITextOverrideStore>();
            var reference = await store.ReadReferenceAsync(cancellationToken).ConfigureAwait(false);
            var overrides = store.Read();

            CliOutput.WriteRows(
            [
                ("Your file", PathDisplay.Show(overrides.Path)),
                ("Status", overrides.Exists ? "present" : "not created yet"),
                ("Reference", PathDisplay.Show(store.ReferencePath)),
                ("Names known", reference is null
                    ? "unknown — start the desktop app once so it can write the reference"
                    : reference.Count.ToString(CultureInfo.InvariantCulture)),
                ("Reworded", overrides.Values.Count.ToString(CultureInfo.InvariantCulture)),
            ]);

            if (overrides.Problem is { Length: > 0 } problem)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(problem);
            }

            foreach (var (key, value) in overrides.Values.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine($"{key}");
                Console.Out.WriteLine($"  now:  {value}");

                if (reference is not null && reference.TryGetValue(key, out var shipped))
                {
                    Console.Out.WriteLine($"  was:  {shipped}");
                }
                else if (reference is not null)
                {
                    Console.Out.WriteLine("  was:  (no such name in this version)");
                }
            }

            return 0;
        });

        return command;
    }

    private static Command CreateInit()
    {
        var force = new Option<bool>("--force")
        {
            Description = "Replace an existing file. It holds edits that exist nowhere else.",
        };

        var command = new Command(
            "init", "Create text.json with every name present and switched off.")
        {
            force,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var store = provider.GetRequiredService<ITextOverrideStore>();

            var reference = await store.ReadReferenceAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ModOperationException(
                    $"There is no '{PathDisplay.Show(store.ReferencePath)}' to work from. Start the desktop " +
                    "application once — it writes that file on every launch — then run this again.",
                    store.ReferencePath);

            var written = await store
                .CreateOverridesAsync(reference, parse.GetValue(force), cancellationToken)
                .ConfigureAwait(false);

            Console.Out.WriteLine(written
                ? $"Wrote {PathDisplay.Show(store.OverridesPath)}. Delete the // in front of a line, change the " +
                  "words after the colon, and restart XXSM."
                : $"'{PathDisplay.Show(store.OverridesPath)}' already exists, so nothing was written. " +
                  "Pass --force to replace it.");

            return 0;
        });

        return command;
    }

    private static Command CreateCheck()
    {
        var command = new Command(
            "check", "Check text.json parses and that every name in it still exists.");

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var store = provider.GetRequiredService<ITextOverrideStore>();
            var overrides = store.Read();

            if (!overrides.Exists)
            {
                Console.Out.WriteLine(
                    $"There is no '{PathDisplay.Show(overrides.Path)}'. XXSM is using its built-in wording.");

                return 0;
            }

            if (overrides.Problem is { Length: > 0 } problem)
            {
                CliOutput.WriteError(problem);
                return ProblemExitCode;
            }

            var reference = await store.ReadReferenceAsync(cancellationToken).ConfigureAwait(false);

            if (reference is null)
            {
                Console.Out.WriteLine(
                    $"'{PathDisplay.Show(overrides.Path)}' parses. Its names could not be checked: start the " +
                    "desktop application once so it can write the reference file.");

                return 0;
            }

            var unknown = overrides.Values.Keys
                .Where(key => !reference.ContainsKey(key))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (unknown.Count > 0)
            {
                CliOutput.WriteError(
                    $"These names in '{PathDisplay.Show(overrides.Path)}' match nothing in this version, so they " +
                    $"change nothing: {string.Join(", ", unknown)}");

                return ProblemExitCode;
            }

            Console.Out.WriteLine(
                $"'{PathDisplay.Show(overrides.Path)}' is good: {overrides.Values.Count} of {reference.Count} " +
                "entries reworded.");

            return 0;
        });

        return command;
    }
}
