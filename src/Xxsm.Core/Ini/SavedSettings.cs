using System.Globalization;
using Serilog;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Ini;

/// <summary>One setting a mod keeps between game sessions: a <c>global persist $name</c> line in <c>[Constants]</c>.</summary>
/// <param name="File">The INI, relative to the mod folder, with <c>/</c> separators.</param>
/// <param name="Line">The declaration's line number, from 1.</param>
/// <param name="Name">The setting's name as written, with its <c>$</c>.</param>
/// <param name="Default">The value the line gives it, or null when it gives none and the game starts it at 0.</param>
/// <param name="InGame">The value the game saved, as the shortest number that reads back the same; null when none.</param>
/// <param name="Original">The default in the mod's kept original, 0 when it gave none; null when there is no kept
/// original or it does not declare this setting.</param>
public sealed record SavedSetting(string File, int Line, string Name, string? Default, string? InGame, string? Original)
{
    /// <summary>The default as the game reads it: <c>0</c> when the line gives none.</summary>
    public string EffectiveDefault => Default ?? "0";

    /// <summary>Whether the game saved a value other than the default.</summary>
    public bool DiffersFromGame => InGame is not null && !SavedSettingValues.SameNumber(EffectiveDefault, InGame);

    /// <summary>Whether the default is no longer the one the mod came with.</summary>
    public bool DiffersFromOriginal => Original is not null && !SavedSettingValues.SameNumber(EffectiveDefault, Original);
}

/// <summary>A mod's saved settings, where the game keeps them, and what could not be read.</summary>
/// <param name="Settings">Every setting, file by file in name order, each file's in file order.</param>
/// <param name="GameSettingsFile">The game's <c>d3dx_user.ini</c>, whether or not it exists yet; null when no
/// <c>d3dx.ini</c> was found above the mod.</param>
/// <param name="Problems">What could not be read, each as a sentence.</param>
public sealed record SavedSettingsReadResult(
    IReadOnlyList<SavedSetting> Settings, string? GameSettingsFile, IReadOnlyList<string> Problems);

/// <summary>One new default for a saved setting.</summary>
/// <param name="File">The INI, as <see cref="SavedSetting.File"/> gave it.</param>
/// <param name="Line">The line, as <see cref="SavedSetting.Line"/> gave it.</param>
/// <param name="ExpectedDefault">The default as it was read, null for none.</param>
/// <param name="Value">The new default: a number.</param>
public sealed record SavedSettingEdit(string File, int Line, string? ExpectedDefault, string Value);

/// <summary>Reads the settings a mod keeps between game sessions, and writes new defaults for them.</summary>
/// <remarks>
/// The game saves each one in the <c>d3dx_user.ini</c> beside its <c>d3dx.ini</c>, under the INI's path or its
/// <c>namespace</c>. A write changes only the default on each declaration line and refuses if one changed since it
/// was read.
/// </remarks>
public interface ISavedSettingsService
{
    /// <summary>Reads a mod's saved settings with their defaults, the game's values and the original defaults.</summary>
    /// <param name="modFolder">The mod folder. A disabled one is read as it is saved when enabled.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ModOperationException">The mod folder does not exist.</exception>
    Task<SavedSettingsReadResult> ReadAsync(string modFolder, CancellationToken cancellationToken = default);

    /// <summary>Writes new defaults, touching nothing else; all are checked before any is written.</summary>
    /// <param name="modFolder">The mod folder.</param>
    /// <param name="edits">The new defaults.</param>
    /// <param name="cancellationToken">Cancels before anything is written.</param>
    /// <exception cref="ModOperationException">A value is not a number, a line is given twice, a file or line is not
    /// a saved setting's, a default changed since it was read, or a file could not be written.</exception>
    /// <exception cref="IniWriteException">A value cannot be stored in the file's own encoding.</exception>
    Task WriteAsync(string modFolder, IReadOnlyList<SavedSettingEdit> edits, CancellationToken cancellationToken = default);
}

/// <summary>Numbers as the game reads and writes them.</summary>
public static class SavedSettingValues
{
    /// <summary>Reads a number the way a <c>[Constants]</c> default is read; false for anything else.</summary>
    public static bool TryParse(string? text, out float value) =>
        float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    /// <summary>Whether two texts are the same number to the game, which keeps 32-bit floats.</summary>
    public static bool SameNumber(string? left, string? right) =>
        TryParse(left, out var a) && TryParse(right, out var b) && a.Equals(b);

    /// <summary>The shortest plain decimal that reads back as <paramref name="value"/>: <c>0.7957</c>, not <c>0.795700014</c>.</summary>
    public static string Shortest(float value)
    {
        if (value == 0)
        {
            return "0";
        }

        var text = value.ToString(CultureInfo.InvariantCulture);

        return text.Contains('E', StringComparison.OrdinalIgnoreCase)
            ? ((decimal)value).ToString(CultureInfo.InvariantCulture)
            : text;
    }
}

/// <summary>The default <see cref="ISavedSettingsService"/>.</summary>
public sealed class SavedSettingsService(IIniFileService files, ILogger logger) : ISavedSettingsService
{
    private const string ConstantsSection = "Constants";
    private const string LoaderIni = "d3dx.ini";
    private const string DefaultGameSettingsIni = "d3dx_user.ini";
    private const int MaximumAncestors = 16;

    private readonly IIniFileService _files = files;
    private readonly ILogger _logger = logger.ForContext<SavedSettingsService>();

    /// <inheritdoc />
    public async Task<SavedSettingsReadResult> ReadAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        var root = ModInis.Root(modFolder);
        var problems = new List<string>();
        var found = new List<(string Relative, string? Prefix, List<Declaration> Declarations, string Path)>();
        var gameRoot = FindGameRoot(root);

        foreach (var file in ModInis.Find(root, problems))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = ModInis.Relative(root, file);
            var document = await TryReadAsync(file, relative, problems, cancellationToken).ConfigureAwait(false);

            if (document is null)
            {
                continue;
            }

            var declarations = Declarations(document);

            if (declarations.Count > 0)
            {
                found.Add((relative, Prefix(document, gameRoot, file), declarations, file));
            }
        }

        if (found.Count == 0)
        {
            return new SavedSettingsReadResult([], null, problems);
        }

        string? gameSettingsFile = null;
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);

        if (gameRoot is null)
        {
            problems.Add(
                $"No {LoaderIni} was found in a folder above this mod, so the game's saved settings cannot be read. " +
                $"XXMI keeps them in {DefaultGameSettingsIni}, beside the Mods folder it loads.");
        }
        else
        {
            gameSettingsFile = await GameSettingsFileAsync(gameRoot, cancellationToken).ConfigureAwait(false);
            await ReadSavedAsync(gameSettingsFile, saved, problems, cancellationToken).ConfigureAwait(false);
        }

        var settings = new List<SavedSetting>();

        foreach (var (relative, prefix, declarations, path) in found)
        {
            var originals = await OriginalDefaultsAsync(root, path, cancellationToken).ConfigureAwait(false);

            foreach (var declaration in declarations)
            {
                var key = prefix is null ? null : $"$\\{prefix}\\{declaration.Name[1..].ToLowerInvariant()}";
                var inGame = key is not null && saved.TryGetValue(key, out var text) && SavedSettingValues.TryParse(text, out var value)
                    ? SavedSettingValues.Shortest(value)
                    : null;

                settings.Add(new SavedSetting(
                    relative,
                    declaration.Line.Number,
                    declaration.Name,
                    declaration.Default,
                    inGame,
                    originals?.GetValueOrDefault(declaration.Name.ToLowerInvariant())));
            }
        }

        return new SavedSettingsReadResult(settings, gameSettingsFile, problems);
    }

    /// <inheritdoc />
    public async Task WriteAsync(string modFolder, IReadOnlyList<SavedSettingEdit> edits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);

        var root = ModInis.Root(modFolder);
        var planned = new List<(string Path, IniDocument Document, List<IniEdit> Edits)>();

        foreach (var group in edits.GroupBy(edit => edit.File, StringComparer.Ordinal))
        {
            var path = ModInis.Resolve(root, group.Key);
            var document = await _files.ReadAsync(path, int.MaxValue, cancellationToken).ConfigureAwait(false);
            var declarations = Declarations(document);
            var fileEdits = new List<IniEdit>();
            var seenLines = new HashSet<int>();

            foreach (var edit in group)
            {
                if (!seenLines.Add(edit.Line))
                {
                    throw new ModOperationException(
                        $"Line {edit.Line} of {PathDisplay.Show(group.Key)} was given more than once, so which value to write " +
                        "is not clear. Nothing was changed.",
                        path);
                }

                var value = ValidValue(edit, group.Key);
                var declaration = declarations.FirstOrDefault(candidate => candidate.Line.Number == edit.Line)
                                  ?? throw new ModOperationException(
                                      $"Line {edit.Line} of {PathDisplay.Show(group.Key)} is not a saved setting any more. " +
                                      "Nothing was changed; read the settings again.",
                                      path);

                if (!string.Equals(declaration.Default, edit.ExpectedDefault, StringComparison.Ordinal))
                {
                    throw new ModOperationException(
                        $"{declaration.Name} on line {edit.Line} of {PathDisplay.Show(group.Key)} now starts at " +
                        $"\"{declaration.Default ?? "0"}\", not \"{edit.ExpectedDefault ?? "0"}\" — the file has changed since it " +
                        "was read. Nothing was changed.",
                        path);
                }

                if (!string.Equals(declaration.Default, value, StringComparison.Ordinal))
                {
                    fileEdits.Add(new IniEdit(declaration.Line, value));
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
                    "Changed the default on line {Line} of {Path} from {Before} to {After}",
                    edit.Entry.Number,
                    path,
                    edit.Entry.Kind == IniLineKind.Entry ? edit.Entry.Value : null,
                    edit.Value);
            }
        }
    }

    /// <summary>The <c>global persist</c> declarations 3DMigoto registers, first of each name only.</summary>
    internal static List<Declaration> Declarations(IniDocument document)
    {
        var found = new List<Declaration>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in document.Sections)
        {
            if (section.IsPreamble || !string.Equals(section.Name, ConstantsSection, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var line in section.Lines)
            {
                if (Parse(line) is { } declaration && names.Add(declaration.Name))
                {
                    found.Add(declaration);
                }
            }
        }

        return found;
    }

    /// <summary>A declaration line as 3DMigoto reads it: flag words, then one <c>$name</c>, then an optional number.</summary>
    private static Declaration? Parse(IniLine line)
    {
        var head = line.Kind switch
        {
            IniLineKind.Entry => line.Key,
            IniLineKind.Directive => line.Text,
            _ => null,
        };

        if (head is null)
        {
            return null;
        }

        var words = head.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var global = false;
        var persist = false;
        var index = 0;

        for (; index < words.Length - 1; index++)
        {
            if (words[index].Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                global = true;
            }
            else if (words[index].Equals("persist", StringComparison.OrdinalIgnoreCase))
            {
                persist = true;
            }
            else if (!words[index].Equals("locked", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        if (!global || !persist || index != words.Length - 1 || !IsVariableName(words[index]))
        {
            return null;
        }

        var value = line.Kind == IniLineKind.Entry && line.Value is { Length: > 0 } given ? given : null;

        // The game skips a declaration whose default is not a number, so it saves nothing for it.
        return value is null || SavedSettingValues.TryParse(value, out _)
            ? new Declaration(line, words[index], value)
            : null;
    }

    private static bool IsVariableName(string word) =>
        word.Length > 1 && word[0] == '$' && word.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string ValidValue(SavedSettingEdit edit, string file)
    {
        var value = edit.Value?.Trim() ?? string.Empty;

        if (!SavedSettingValues.TryParse(value, out _) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            throw new ModOperationException(
                $"\"{value}\" for line {edit.Line} of {PathDisplay.Show(file)} is not a number, and a saved setting's default " +
                "has to be one, such as 0, 1 or 0.5. Nothing was changed.");
        }

        return value;
    }

    /// <summary>The folder holding <c>d3dx.ini</c> above the mod, or null.</summary>
    private static string? FindGameRoot(string root)
    {
        var directory = Path.GetDirectoryName(root);

        for (var depth = 0; directory is not null && depth < MaximumAncestors; depth++)
        {
            if (PathComparer.TryResolveExisting(Path.Combine(directory, LoaderIni), out var loader) && File.Exists(loader))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>The name the game files an INI's settings under: its namespace, or its path from the game's folder.</summary>
    private static string? Prefix(IniDocument document, string? gameRoot, string file)
    {
        // A namespace counts only before the first section; a later one renames again.
        var preamble = document.Sections.FirstOrDefault(section => section.IsPreamble);

        if (preamble?.GetAll("namespace") is { Count: > 0 } namespaces && namespaces[^1].Value is { Length: > 0 } named)
        {
            return named.ToLowerInvariant();
        }

        if (gameRoot is null || PathComparer.TryGetRelativePath(gameRoot, file) is not { } relative)
        {
            return null;
        }

        var parts = relative.Replace('\\', '/').Split('/');

        // A disabled mod is saved under the name it has when enabled.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            parts[i] = ModsFolderLayout.StripDisabledPrefix(parts[i]);
        }

        return string.Join('\\', parts).ToLowerInvariant();
    }

    /// <summary>The game's settings file: <c>[Include] user_config</c> in <c>d3dx.ini</c>, or <c>d3dx_user.ini</c>.</summary>
    private async Task<string> GameSettingsFileAsync(string gameRoot, CancellationToken cancellationToken)
    {
        var name = DefaultGameSettingsIni;

        if (PathComparer.TryResolveExisting(Path.Combine(gameRoot, LoaderIni), out var loader))
        {
            try
            {
                var document = await _files.ReadAsync(loader, ModScanBounds.Default.MaxIniBytes, cancellationToken).ConfigureAwait(false);

                if (document.FindSection("Include")?.GetValue("user_config") is { Length: > 0 } configured &&
                    !Path.IsPathRooted(configured) && !configured.Contains(':', StringComparison.Ordinal))
                {
                    name = configured.Replace('\\', '/');
                }
            }
            catch (ModOperationException ex)
            {
                _logger.Warning(ex, "Could not read {Path}; looking for the game's settings in {Name}", loader, name);
            }
        }

        var path = Path.GetFullPath(Path.Combine(gameRoot, name));

        return PathComparer.TryResolveExisting(path, out var resolved) ? resolved : path;
    }

    private async Task ReadSavedAsync(
        string path, Dictionary<string, string> saved, List<string> problems, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var document = await TryReadAsync(path, Path.GetFileName(path), problems, cancellationToken).ConfigureAwait(false);

        foreach (var section in document?.SectionsNamed(ConstantsSection) ?? [])
        {
            foreach (var entry in section.Entries)
            {
                if (entry.Key is { Length: > 2 } key && key.StartsWith("$\\", StringComparison.Ordinal) && entry.Value is { } value)
                {
                    // The game reads the file in order, so a later line wins.
                    saved[key.ToLowerInvariant()] = value;
                }
            }
        }
    }

    /// <summary>Each setting's default in the kept original, by lower-case name; null when there is no original.</summary>
    private async Task<Dictionary<string, string>?> OriginalDefaultsAsync(string root, string path, CancellationToken cancellationToken)
    {
        var original = ModInis.OriginalOf(root, path);

        if (!File.Exists(original))
        {
            return null;
        }

        try
        {
            var document = await _files.ReadAsync(original, ModScanBounds.Default.MaxIniBytes, cancellationToken).ConfigureAwait(false);

            return Declarations(document).ToDictionary(
                declaration => declaration.Name.ToLowerInvariant(),
                declaration => declaration.Default ?? "0",
                StringComparer.Ordinal);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Could not read the kept original {Path}", original);
            return null;
        }
    }

    private async Task<IniDocument?> TryReadAsync(string path, string shown, List<string> problems, CancellationToken cancellationToken)
    {
        try
        {
            var document = await _files.ReadAsync(path, ModScanBounds.Default.MaxIniBytes, cancellationToken).ConfigureAwait(false);

            if (document.Truncated)
            {
                problems.Add($"{PathDisplay.Show(shown)} is larger than {ModScanBounds.Default.MaxIniBytes / (1024 * 1024)} MB and was not read.");
                return null;
            }

            return document;
        }
        catch (ModOperationException ex)
        {
            problems.Add($"{PathDisplay.Show(shown)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>One <c>global persist</c> declaration: its line, its <c>$name</c> and its default, null for none.</summary>
    internal sealed record Declaration(IniLine Line, string Name, string? Default);
}
