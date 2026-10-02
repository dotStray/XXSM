using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;
using Xxsm.Core.Text;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm mod keys</c>: a mod's key bindings, changed without touching the rest of the INI.</summary>
internal static class ModKeysCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var folder = new Argument<string>("folder") { Description = "The mod folder." };

        var section = new Option<string?>("--section")
        {
            Description = "The binding to change, by its section name without brackets, e.g. KeySwap.",
        };

        var file = new Option<string?>("--file")
        {
            Description = "The INI the section is in, relative to the mod folder. Needed only when two files use the same name.",
        };

        var key = new Option<string[]>("--key")
        {
            Description = "The new key. Repeat it for a section with several key lines (keyboard and controller), in order.",
        };

        var back = new Option<string?>("--back") { Description = "The new key that steps backward." };

        var variable = new Option<string[]>("--var")
        {
            Description = "A variable's new values, as $name=values, e.g. '$swapvar=0,1,2'. Repeat it for several.",
        };

        var command = new Command(
            "keys",
            "List a mod's key bindings, or change one. Only the values named change; every other byte of the INI stays.")
        {
            folder,
            section,
            file,
            key,
            back,
            variable,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var keys = provider.GetRequiredService<IKeySwapService>();
            var modFolder = Path.GetFullPath(parse.GetValue(folder)!);

            var read = await keys.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);
            var changing = parse.GetValue(key) is { Length: > 0 } || parse.GetValue(back) is not null ||
                           parse.GetValue(variable) is { Length: > 0 };

            if (changing)
            {
                var edits = Edits(
                    read,
                    parse.GetValue(section),
                    parse.GetValue(file),
                    parse.GetValue(key) ?? [],
                    parse.GetValue(back),
                    parse.GetValue(variable) ?? []);

                await keys.WriteAsync(modFolder, edits, cancellationToken).ConfigureAwait(false);
                read = await keys.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false);
            }

            var report = new ModKeysReport(
                modFolder,
                changing,
                [
                    .. read.Sections.Select(binding => new ModKeySectionReport(
                        binding.File,
                        binding.Section,
                        binding.Type,
                        [.. binding.Fields.Select(field => new ModKeyFieldReport(
                            CliOutput.Camel(field.Kind.ToString()), field.Name, field.Line, field.Value))])),
                ],
                read.Problems);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            if (report.Sections.Count == 0)
            {
                Console.Out.WriteLine("This mod has no key bindings.");
            }

            foreach (var binding in report.Sections)
            {
                Console.Out.WriteLine($"[{binding.Section}]  {PathDisplay.Show(binding.File)}" + (binding.Type is { } type ? $"  ({type})" : string.Empty));

                foreach (var field in binding.Fields)
                {
                    Console.Out.WriteLine($"  {field.Name,-14} = {field.Value}");
                }
            }

            foreach (var problem in report.Problems)
            {
                CliOutput.WriteError(problem);
            }

            if (changing)
            {
                Console.Out.WriteLine("Saved.");
            }

            return 0;
        });

        return command;
    }

    private static List<KeySwapEdit> Edits(
        KeySwapReadResult read,
        string? sectionName,
        string? fileName,
        string[] keys,
        string? back,
        string[] variables)
    {
        if (sectionName is not { Length: > 0 })
        {
            throw new ModOperationException("Say which binding to change with --section, e.g. --section KeySwap.");
        }

        var matches = read.Sections
            .Where(candidate => string.Equals(candidate.Section, sectionName, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => fileName is null || string.Equals(candidate.File, fileName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var binding = matches.Count switch
        {
            0 => throw new ModOperationException($"This mod has no key binding [{sectionName}]" +
                                                 (fileName is null ? "." : $" in {fileName}.")),
            1 => matches[0],
            _ => throw new ModOperationException(
                $"[{sectionName}] is in more than one INI ({string.Join(", ", matches.Select(match => match.File))}). " +
                "Say which with --file."),
        };

        var edits = new List<KeySwapEdit>();
        var keyFields = binding.Fields.Where(field => field.Kind == KeySwapFieldKind.Key).ToList();

        if (keys.Length > keyFields.Count)
        {
            throw new ModOperationException(
                $"[{sectionName}] has {EnglishCount.Plural(keyFields.Count, "key line", "key lines")}, so it takes at most that many --key.");
        }

        edits.AddRange(keys.Select((value, index) => Edit(binding, keyFields[index], value)));

        if (back is not null)
        {
            var field = binding.Fields.FirstOrDefault(candidate => candidate.Kind == KeySwapFieldKind.Back)
                        ?? throw new ModOperationException(
                            $"[{sectionName}] has no back line to change. XXSM changes values; it does not add lines to a mod's INI.");
            edits.Add(Edit(binding, field, back));
        }

        foreach (var assignment in variables)
        {
            var split = assignment.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0)
            {
                throw new ModOperationException($"--var takes $name=values, e.g. '$swapvar=0,1,2', not '{assignment}'.");
            }

            var name = assignment[..split].Trim();
            var field = binding.Fields.FirstOrDefault(candidate =>
                            candidate.Kind == KeySwapFieldKind.Variable &&
                            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ModOperationException($"[{sectionName}] sets no variable called {name}.");

            edits.Add(Edit(binding, field, assignment[(split + 1)..]));
        }

        return edits;
    }

    private static KeySwapEdit Edit(KeySwapSection binding, KeySwapField field, string value) =>
        new(binding.File, field.Line, field.Value, value);
}
