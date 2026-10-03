using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Ini;

/// <summary>Which part of a key binding a field is.</summary>
public enum KeySwapFieldKind
{
    /// <summary>A <c>key</c> line: the key that steps forward. There may be several.</summary>
    Key,

    /// <summary>A <c>back</c> line: the key that steps backward.</summary>
    Back,

    /// <summary>A <c>$variable</c> line: the values the binding cycles through or sets.</summary>
    Variable,
}

/// <summary>One editable line of a key binding.</summary>
/// <param name="Kind">Which part of the binding it is.</param>
/// <param name="Name">The setting's name as written: <c>key</c>, <c>back</c> or <c>$swapvar</c>.</param>
/// <param name="Line">Its line number in the file, from 1.</param>
/// <param name="Value">Its value as written, trimmed.</param>
public sealed record KeySwapField(KeySwapFieldKind Kind, string Name, int Line, string Value);

/// <summary>One key binding in a mod: a <c>[Key…]</c> section with a <c>key</c> line.</summary>
/// <param name="File">The INI, relative to the mod folder, with <c>/</c> separators.</param>
/// <param name="Section">The section's name, without brackets.</param>
/// <param name="Type">Its <c>type</c> (<c>cycle</c>, <c>toggle</c>, <c>hold</c>), or null.</param>
/// <param name="Fields">The <c>key</c>, <c>back</c> and <c>$variable</c> lines, in file order.</param>
public sealed record KeySwapSection(string File, string Section, string? Type, IReadOnlyList<KeySwapField> Fields);

/// <summary>A mod's key bindings, and the INIs that could not be read for them.</summary>
/// <param name="Sections">Every binding, file by file in name order, each file's in file order.</param>
/// <param name="Problems">INIs that were skipped, each with why.</param>
public sealed record KeySwapReadResult(IReadOnlyList<KeySwapSection> Sections, IReadOnlyList<string> Problems);

/// <summary>One change to a key binding's line.</summary>
/// <param name="File">The INI, relative to the mod folder, as <see cref="KeySwapSection.File"/> gave it.</param>
/// <param name="Line">The line number, as <see cref="KeySwapField.Line"/> gave it.</param>
/// <param name="ExpectedValue">The value the line had when it was read.</param>
/// <param name="Value">The value to write.</param>
public sealed record KeySwapEdit(string File, int Line, string ExpectedValue, string Value);

/// <summary>Reads the key bindings in a mod's INIs and changes them surgically.</summary>
/// <remarks>
/// A binding is any <c>[Key…]</c> section with a <c>key</c> line in an INI 3DMigoto loads. A write refuses if any
/// line it changes is not what it was when read.
/// </remarks>
public interface IKeySwapService
{
    /// <summary>Reads a mod's key bindings.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bindings, and any INI that could not be read.</returns>
    /// <exception cref="ModOperationException">The mod folder does not exist.</exception>
    Task<KeySwapReadResult> ReadAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Writes changed values back, touching nothing else; all are checked before any is written.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="edits">The changes.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">A value is empty or has a line break, a line is given twice, a file or
    /// line is not a binding's, a line changed since it was read, or a file could not be written.</exception>
    /// <exception cref="IniWriteException">A value cannot be stored in the file's own encoding.</exception>
    Task WriteAsync(string modFolder, IReadOnlyList<KeySwapEdit> edits, CancellationToken cancellationToken = default);
}

/// <summary>The default <see cref="IKeySwapService"/>.</summary>
public sealed class KeySwapService(IIniFileService files, ILogger logger) : IKeySwapService
{
    private const string SectionPrefix = "Key";

    private readonly IIniFileService _files = files;
    private readonly ILogger _logger = logger.ForContext<KeySwapService>();

    /// <inheritdoc />
    public async Task<KeySwapReadResult> ReadAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        var root = ModInis.Root(modFolder);
        var sections = new List<KeySwapSection>();
        var problems = new List<string>();

        foreach (var file in ModInis.Find(root, problems))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = ModInis.Relative(root, file);
            IniDocument document;

            try
            {
                document = await _files.ReadAsync(file, ModScanBounds.Default.MaxIniBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ModOperationException ex)
            {
                problems.Add($"{PathDisplay.Show(relative)}: {ex.Message}");
                continue;
            }

            if (document.Truncated)
            {
                problems.Add($"{PathDisplay.Show(relative)} is larger than {ModScanBounds.Default.MaxIniBytes / (1024 * 1024)} MB and was not read for key bindings.");
                continue;
            }

            sections.AddRange(Bindings(document, relative));
        }

        return new KeySwapReadResult(sections, problems);
    }

    /// <inheritdoc />
    public async Task WriteAsync(string modFolder, IReadOnlyList<KeySwapEdit> edits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);

        var root = ModInis.Root(modFolder);
        var planned = new List<(string Path, IniDocument Document, List<IniEdit> Edits)>();

        foreach (var group in edits.GroupBy(edit => edit.File, StringComparer.Ordinal))
        {
            var path = ModInis.Resolve(root, group.Key);
            var document = await _files.ReadAsync(path, int.MaxValue, cancellationToken).ConfigureAwait(false);
            var bindings = Bindings(document, group.Key).SelectMany(section => section.Fields).ToList();
            var fileEdits = new List<IniEdit>();
            var seenLines = new HashSet<int>();

            foreach (var edit in group)
            {
                if (!seenLines.Add(edit.Line))
                {
                    throw new ModOperationException(
                        $"Line {edit.Line} of {group.Key} was given more than once, so which value to write is " +
                        "not clear. Give each line one value. Nothing was changed.",
                        path);
                }

                var value = ValidValue(edit, group.Key);
                var field = bindings.FirstOrDefault(candidate => candidate.Line == edit.Line)
                            ?? throw new ModOperationException(
                                $"Line {edit.Line} of {group.Key} is not part of a key binding any more. " +
                                "Nothing was changed; read the bindings again.",
                                path);

                if (!string.Equals(field.Value, edit.ExpectedValue, StringComparison.Ordinal))
                {
                    throw new ModOperationException(
                        $"{field.Name} on line {edit.Line} of {group.Key} is now \"{field.Value}\", not " +
                        $"\"{edit.ExpectedValue}\" — the file has changed since it was read. Nothing was changed.",
                        path);
                }

                if (!string.Equals(field.Value, value, StringComparison.Ordinal))
                {
                    fileEdits.Add(new IniEdit(document.Lines.First(line => line.Number == edit.Line), value));
                }
            }

            if (fileEdits.Count > 0)
            {
                planned.Add((path, document, fileEdits));
            }
        }

        var rewritten = planned.Select(item => (item.Path, item.Edits, Bytes: IniEditor.Apply(item.Document, item.Edits))).ToList();

        foreach (var (path, fileEdits, bytes) in rewritten)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ModInis.KeepOriginal(root, path, _logger);
            await _files.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);

            foreach (var edit in fileEdits)
            {
                _logger.Information(
                    "Changed {Key} on line {Line} of {Path} from {Before} to {After}",
                    edit.Entry.Key,
                    edit.Entry.Number,
                    path,
                    edit.Entry.Value,
                    edit.Value);
            }
        }
    }

    private static IEnumerable<KeySwapSection> Bindings(IniDocument document, string relative)
    {
        foreach (var section in document.Sections)
        {
            if (section.IsPreamble || !section.NameStartsWith(SectionPrefix) || section.Find("key") is null)
            {
                continue;
            }

            var fields = new List<KeySwapField>();

            foreach (var entry in section.Entries)
            {
                var kind = entry.Key switch
                {
                    { } key when key.Equals("key", StringComparison.OrdinalIgnoreCase) => KeySwapFieldKind.Key,
                    { } key when key.Equals("back", StringComparison.OrdinalIgnoreCase) => KeySwapFieldKind.Back,
                    { Length: > 1 } key when key[0] == '$' => KeySwapFieldKind.Variable,
                    _ => (KeySwapFieldKind?)null,
                };

                if (kind is { } found)
                {
                    fields.Add(new KeySwapField(found, entry.Key!, entry.Number, entry.Value ?? string.Empty));
                }
            }

            yield return new KeySwapSection(relative, section.Name, section.GetValue("type"), fields);
        }
    }

    private static string ValidValue(KeySwapEdit edit, string file)
    {
        var value = edit.Value?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            throw new ModOperationException(
                $"Line {edit.Line} of {PathDisplay.Show(file)} cannot be left empty: a binding with no key or value does nothing, " +
                "and 3DMigoto may complain. Nothing was changed.");
        }

        if (value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new ModOperationException(
                $"The value for line {edit.Line} of {PathDisplay.Show(file)} has a line break in it, which would split the setting in two. " +
                "Nothing was changed.");
        }

        return value;
    }
}
