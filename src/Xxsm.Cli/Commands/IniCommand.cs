using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Cli.Output;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;

namespace Xxsm.Cli.Commands;

/// <summary><c>xxsm ini</c>: read and surgically edit a 3DMigoto INI.</summary>
internal static class IniCommand
{
    /// <summary>Builds the command.</summary>
    public static Command Create()
    {
        var command = new Command("ini", "Read and edit 3DMigoto INI files.")
        {
            CreateShow(),
            CreateSet(),
        };

        return command;
    }

    private static Command CreateShow()
    {
        var file = new Argument<string>("file") { Description = "The .ini file to read." };
        var showEntries = new Option<bool>("--entries")
        {
            Description = "List every setting, not just the section headings.",
        };

        var command = new Command("show", "Parse an INI and report its structure.") { file, showEntries };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));

            var path = parse.GetValue(file)!;
            var document = await provider
                .GetRequiredService<IIniFileService>()
                .ReadAsync(path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var report = Describe(path, document);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            WriteShowHuman(report, parse.GetValue(showEntries));
            return 0;
        });

        return command;
    }

    private static Command CreateSet()
    {
        var file = new Argument<string>("file") { Description = "The .ini file to edit." };
        var section = new Argument<string>("section") { Description = "The section name, without brackets." };
        var key = new Argument<string>("key") { Description = "The setting name." };
        var value = new Argument<string>("value") { Description = "The new value." };
        var dryRun = new Option<bool>("--dry-run")
        {
            Description = "Report what would change without writing anything.",
        };

        var command = new Command("set", "Change one setting's value, leaving every other byte alone.")
        {
            file,
            section,
            key,
            value,
            dryRun,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            await using var provider = CliHost.Build(parse.GetValue(GlobalOptions.Verbose));
            var files = provider.GetRequiredService<IIniFileService>();

            var path = parse.GetValue(file)!;
            var sectionName = parse.GetValue(section)!;
            var keyName = parse.GetValue(key)!;
            var newValue = parse.GetValue(value)!;
            var pretend = parse.GetValue(dryRun);

            var document = await files.ReadAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);

            var entry = document.FindSection(sectionName)?.Find(keyName);
            if (entry is null)
            {
                CliOutput.WriteError(
                    document.FindSection(sectionName) is null
                        ? $"{PathDisplay.Show(path)} has no [{sectionName}] section."
                        : $"[{sectionName}] in {PathDisplay.Show(path)} has no '{keyName}' setting.");
                return 1;
            }

            var before = document.Length;
            var after = IniEditor.SetValue(document, entry, newValue);
            var newByteCount = after.Length - before + entry.ValueSpan.Length;

            if (!pretend)
            {
                await files.WriteAsync(path, after, cancellationToken).ConfigureAwait(false);
            }

            var report = new IniSetReport(
                path, entry.SectionIndex >= 0 ? document.Sections[entry.SectionIndex].Name : sectionName,
                entry.Key!, entry.Number, entry.Value!, newValue,
                entry.ValueSpan.Start, entry.ValueSpan.Length, newByteCount,
                before - entry.ValueSpan.Length, !pretend);

            if (parse.GetValue(GlobalOptions.Json))
            {
                return CliJson.Write(report);
            }

            CliOutput.WriteRows(
            [
                ("File", PathDisplay.Show(report.Path)),
                ("Setting", $"[{report.Section}] {report.Key}   (line {report.Line.ToString(CultureInfo.InvariantCulture)})"),
                ("Was", report.OldValue.Length == 0 ? "(empty)" : report.OldValue),
                ("Now", report.NewValue.Length == 0 ? "(empty)" : report.NewValue),
                ("Replaced", $"{report.OldByteCount.ToString(CultureInfo.InvariantCulture)} bytes at offset {report.Offset.ToString(CultureInfo.InvariantCulture)} with {report.NewByteCount.ToString(CultureInfo.InvariantCulture)} bytes"),
                ("Left untouched", $"{report.UntouchedByteCount.ToString(CultureInfo.InvariantCulture)} of the file's {before.ToString(CultureInfo.InvariantCulture)} bytes"),
                ("Written", report.Written ? "yes" : "no — this was a dry run"),
            ]);

            return 0;
        });

        return command;
    }

    private static IniReport Describe(string path, IniDocument document) => new(
        path,
        document.Length,
        document.Encoding.ToString(),
        document.LineEndings.ToString(),
        document.Lines.Length,
        document.Sections.Length - 1,
        document.Lines.Count(line => line.Kind == IniLineKind.Entry),
        document.Lines.Count(line => line.Kind == IniLineKind.Directive),
        document.Lines.Count(line => line.Kind == IniLineKind.Comment),
        document.ToBytes().AsSpan().SequenceEqual(document.Bytes),
        [
            .. document.Sections
                .Where(section => !section.IsPreamble)
                .Select(section => new IniReportSection(
                    section.Name,
                    section.Header!.Number,
                    [.. section.Entries.Select(entry => new IniReportEntry(entry.Key!, entry.Value!, entry.Number))])),
        ],
        [.. document.Diagnostics.Select(diagnostic => new ScanDiagnostic(
            diagnostic.Severity.ToString().ToLowerInvariant(),
            diagnostic.Code,
            diagnostic.Message,
            path,
            diagnostic.Line > 0 ? $"line {diagnostic.Line.ToString(CultureInfo.InvariantCulture)}" : null))]);

    private static void WriteShowHuman(IniReport report, bool showEntries)
    {
        CliOutput.WriteRows(
        [
            ("File", PathDisplay.Show(report.Path)),
            ("Size", $"{report.Bytes.ToString(CultureInfo.InvariantCulture)} bytes"),
            ("Text", $"{report.Encoding}, {report.LineEndings} line endings"),
            ("Lines", report.LineCount.ToString(CultureInfo.InvariantCulture)),
            ("Sections", report.SectionCount.ToString(CultureInfo.InvariantCulture)),
            ("Settings", report.EntryCount.ToString(CultureInfo.InvariantCulture)),
            ("Command-list lines", report.DirectiveCount.ToString(CultureInfo.InvariantCulture)),
            ("Comments", report.CommentCount.ToString(CultureInfo.InvariantCulture)),
            ("Round-trips exactly", report.RoundTrips ? "yes" : "NO — this is a bug, please report it"),
        ]);

        Console.Out.WriteLine();

        foreach (var section in report.Sections)
        {
            Console.Out.WriteLine($"  {section.Line,5}  [{section.Name}]");

            if (!showEntries)
            {
                continue;
            }

            foreach (var entry in section.Entries)
            {
                Console.Out.WriteLine($"  {entry.Line,5}    {entry.Key} = {entry.Value}");
            }
        }

        if (report.Diagnostics.Count == 0)
        {
            return;
        }

        Console.Out.WriteLine();
        foreach (var diagnostic in report.Diagnostics)
        {
            Console.Out.WriteLine($"  [{diagnostic.Severity}] {diagnostic.Subject ?? "file"}: {diagnostic.Message}");
        }
    }
}
