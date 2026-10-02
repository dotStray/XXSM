using Xxsm.Core.Diagnostics;
using Xxsm.Core.Hashes;
using Xxsm.Core.Ini;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModSignalExtractor"/>: every INI in the mod, recorded, never judged.</summary>
public sealed class ModSignalExtractor(IIniFileService iniFiles) : IModSignalExtractor
{
    private static readonly string[] AssetExtensions = [".ib", ".buf", ".dds"];

    private const string TextureOverride = "TextureOverride";
    private const string ShaderOverride = "ShaderOverride";

    private readonly IIniFileService _iniFiles = iniFiles;

    /// <inheritdoc />
    public async Task<ModSignals> ExtractAsync(
        string root, ModScanBounds? bounds = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        var limits = bounds ?? ModScanBounds.Default;
        if (!Directory.Exists(root))
        {
            throw new ModOperationException($"There is no mod folder at {PathDisplay.Show(root)}.", root);
        }

        var state = new ScanState(root, limits);
        await WalkAsync(state, root, depth: 0, cancellationToken).ConfigureAwait(false);
        return state.ToSignals();
    }

    private async Task WalkAsync(
        ScanState state, string directory, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (PathComparer.AreEqual(directory, state.Root))
            {
                throw new ModOperationException(
                    $"Could not list the mod folder at {PathDisplay.Show(directory)}: {exception.Message}", directory, exception);
            }

            state.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.UnreadableDirectory,
                $"Could not look inside {state.Relative(directory)}: {exception.Message}"));
            return;
        }

        // Ordinal, so two scans of one folder list the same entries in the same order.
        Array.Sort(entries, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(entry))
            {
                if (new DirectoryInfo(entry).LinkTarget is not null)
                {
                    // A link can point back up the tree: noted and skipped, never followed.
                    state.Add(new Diagnostic(
                        DiagnosticSeverity.Info,
                        ModDiagnosticCodes.SkippedSymlink,
                        $"{state.Relative(entry)} is a link to somewhere else and was not followed."));
                    continue;
                }

                if (depth + 1 > state.Bounds.MaxDepth)
                {
                    state.Reached(ModScanLimit.Depth);
                    continue;
                }

                await WalkAsync(state, entry, depth + 1, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (state.FilesSeen >= state.Bounds.MaxFiles)
            {
                state.Reached(ModScanLimit.FileCount);
                return;
            }

            state.CountFile();
            await ReadFileAsync(state, entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadFileAsync(ScanState state, string file, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(file);

        if (AssetExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            state.AddAsset(Path.GetFileNameWithoutExtension(file));
            return;
        }

        if (string.Equals(name, "hash.json", StringComparison.OrdinalIgnoreCase))
        {
            await ReadHashJsonAsync(state, file, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(extension, ".ini", StringComparison.OrdinalIgnoreCase))
        {
            await ReadIniAsync(state, file, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadIniAsync(ScanState state, string file, CancellationToken cancellationToken)
    {
        var budget = state.RemainingIniBytes;
        if (budget <= 0)
        {
            state.Reached(ModScanLimit.IniBytes);
            return;
        }

        IniDocument document;
        try
        {
            document = await _iniFiles.ReadAsync(file, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (ModOperationException exception)
        {
            state.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.UnreadableFile,
                $"Could not read {state.Relative(file)}: {exception.Message}"));
            return;
        }

        var relative = state.Relative(file);
        state.AddIni(relative, document.Length);

        if (document.Truncated)
        {
            state.Reached(ModScanLimit.IniBytes);
        }

        foreach (var diagnostic in document.Diagnostics)
        {
            state.Add(diagnostic with { Message = $"{PathDisplay.Show(relative)}: {diagnostic.Message}" });
        }

        foreach (var section in document.Sections)
        {
            if (!section.NameStartsWith(TextureOverride) && !section.NameStartsWith(ShaderOverride))
            {
                continue;
            }

            var hashes = new List<string>();
            foreach (var entry in section.GetAll("hash"))
            {
                var hash = HashText.Extract(entry.Value);
                if (hash is null)
                {
                    state.Add(new Diagnostic(
                        DiagnosticSeverity.Warning,
                        ModDiagnosticCodes.UnreadableHash,
                        $"{PathDisplay.Show(relative)}: \"{entry.Value}\" in [{section.Name}] is not an 8 or 16 digit "
                        + "hash, so it was ignored.",
                        entry.Number));
                    continue;
                }

                if (!hashes.Contains(hash, StringComparer.Ordinal))
                {
                    hashes.Add(hash);
                }
            }

            if (hashes.Count > 1)
            {
                state.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    ModDiagnosticCodes.ConflictingHashes,
                    $"{PathDisplay.Show(relative)}: [{section.Name}] gives more than one hash "
                    + $"({string.Join(", ", hashes)}). 3DMigoto uses the first; all of them were "
                    + "kept as evidence.",
                    section.Header?.Number ?? 0));
            }

            state.AddSection(new ModOverrideSection(
                section.Name,
                hashes,
                section.GetValue("match_first_index"),
                section.GetValue("match_priority"),
                relative,
                section.Header?.Number ?? 0));

            state.AddHashes(hashes);
        }
    }

    private static async Task ReadHashJsonAsync(ScanState state, string file, CancellationToken cancellationToken)
    {
        var relative = state.Relative(file);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                ModDiagnosticCodes.UnreadableFile,
                $"Could not read {PathDisplay.Show(relative)}: {exception.Message}"));
            return;
        }

        var result = UpstreamHashReader.Read(bytes, relative);
        foreach (var diagnostic in result.Diagnostics)
        {
            state.Add(diagnostic);
        }

        if (result.Components.Count > 0)
        {
            state.AddHashJson(relative);
            state.AddHashes(result.Hashes());
        }
    }

    private sealed class ScanState(string root, ModScanBounds bounds)
    {
        private readonly List<string> _hashes = [];
        private readonly HashSet<string> _seenHashes = new(StringComparer.Ordinal);
        private readonly List<ModOverrideSection> _sections = [];
        private readonly List<string> _assets = [];
        private readonly HashSet<string> _seenAssets = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _iniFiles = [];
        private readonly List<string> _hashJsonFiles = [];
        private readonly List<Diagnostic> _diagnostics = [];

        public string Root { get; } = root;

        public ModScanBounds Bounds { get; } = bounds;

        public int FilesSeen { get; private set; }

        public long IniBytesRead { get; private set; }

        public ModScanLimit LimitsReached { get; private set; } = ModScanLimit.None;

        public int RemainingIniBytes => (int)Math.Max(0, Bounds.MaxIniBytes - IniBytesRead);

        public void CountFile() => FilesSeen++;

        public void Reached(ModScanLimit limit) => LimitsReached |= limit;

        public void Add(Diagnostic diagnostic) => _diagnostics.Add(diagnostic);

        public void AddSection(ModOverrideSection section) => _sections.Add(section);

        public void AddIni(string relative, int bytes)
        {
            _iniFiles.Add(relative);
            IniBytesRead += bytes;
        }

        public void AddHashJson(string relative) => _hashJsonFiles.Add(relative);

        public void AddAsset(string name)
        {
            if (name.Length > 0 && _seenAssets.Add(name))
            {
                _assets.Add(name);
            }
        }

        public void AddHashes(IEnumerable<string> hashes)
        {
            foreach (var hash in hashes)
            {
                if (_seenHashes.Add(hash))
                {
                    _hashes.Add(hash);
                }
            }
        }

        public string Relative(string path) =>
            PathComparer.TryGetRelativePath(Root, path) ?? PathComparer.Normalize(path);

        public ModSignals ToSignals()
        {
            _hashes.Sort(StringComparer.Ordinal);
            _assets.Sort(StringComparer.OrdinalIgnoreCase);

            return new ModSignals
            {
                Root = Root,
                Hashes = _hashes,
                OverrideSections = _sections,
                AssetNames = _assets,
                IniFiles = _iniFiles,
                HashJsonFiles = _hashJsonFiles,
                FilesSeen = FilesSeen,
                IniBytesRead = IniBytesRead,
                LimitsReached = LimitsReached,
                Diagnostics = _diagnostics,
            };
        }
    }
}
