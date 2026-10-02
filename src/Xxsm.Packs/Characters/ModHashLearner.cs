using Serilog;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Packs.Hashes;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Characters;

/// <summary>The default <see cref="IModHashLearner"/>: the mod walk and the sorter, with hash kinds read.</summary>
public sealed class ModHashLearner(IModSignalExtractor signals, IModSorter sorter, ILogger logger) : IModHashLearner
{
    private readonly IModSignalExtractor _signals = signals;
    private readonly IModSorter _sorter = sorter;
    private readonly ILogger _logger = logger.ForContext<ModHashLearner>();

    /// <inheritdoc />
    public async Task<LearnedFromMod> LearnAsync(
        string modFolder,
        GameData data,
        SortRequest request = default,
        SortSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentNullException.ThrowIfNull(data);

        var signals = await _signals.ExtractAsync(modFolder, bounds: null, cancellationToken)
            .ConfigureAwait(false);

        var index = SortIndex.Build(data, settings);
        var decision = _sorter.Sort(index, signals, request);

        var diagnostics = new List<Diagnostic>(signals.Diagnostics);
        var hashes = new List<ParsedHash>();
        var files = new List<string>();

        foreach (var relative in signals.IniFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadAsync(signals.Root, relative, isJson: false, hashes, files, diagnostics, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var relative in signals.HashJsonFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReadAsync(signals.Root, relative, isJson: true, hashes, files, diagnostics, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.Information(
            "Learned {Hashes} hashes from {ModFolder} across {Files} files; the sorter says {Variant}",
            hashes.Count,
            modFolder,
            files.Count,
            decision.VariantId ?? "nothing");

        return new LearnedFromMod
        {
            ModFolder = signals.Root,
            SuggestedName = Suggest(request.FolderName ?? signals.Root),
            Hashes = hashes,
            Files = files,
            Decision = decision,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Reads one file that may carry hashes, adding what it yields.</summary>
    internal static async Task ReadAsync(
        string root,
        string relative,
        bool isJson,
        List<ParsedHash> hashes,
        List<string> files,
        List<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var path = PathComparer.Normalize(Path.Combine(root, relative));

        try
        {
            var result = isJson
                ? HashPaste.ParseHashJson(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), relative)
                : HashPaste.Parse(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), relative);

            if (result.Hashes.Count == 0)
            {
                return;
            }

            files.Add(relative);

            foreach (var parsed in result.Hashes)
            {
                if (!hashes.Any(existing =>
                        string.Equals(existing.Entry.Hash, parsed.Entry.Hash, StringComparison.OrdinalIgnoreCase)
                        && existing.Entry.Kind == parsed.Entry.Kind))
                {
                    hashes.Add(parsed);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.UnreadableFile,
                $"Could not re-read '{PathDisplay.Show(relative)}' for its hash kinds: {exception.Message}. " +
                "Its hashes are not included."));
        }
    }

    /// <summary>A name to prefill a new character with, split from the folder name as the sorter splits it.</summary>
    private static string Suggest(string modFolderOrName)
    {
        var folder = Path.GetFileName(PathComparer.Normalize(modFolderOrName).TrimEnd('/'));

        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        folder = ModsFolderLayout.StripDisabledPrefix(folder);

        // Only the camel-case parts: Tokenize returns the whole chunk first, then its pieces.
        var parts = SortName.Tokenize(folder)
            .Where(token => token.Level == SortName.PartLevel)
            .Select(token => token.Text)
            .ToList();

        return parts.Count == 0 ? folder : string.Join(' ', parts);
    }
}
