using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModPreviewSource"/>.</summary>
public sealed class ModPreviewSource(ILogger logger) : IModPreviewSource
{
    /// <summary>Name endings that mean a texture map or a signature overlay, not a picture of the mod.</summary>
    private static readonly string[] NotAPreviewSuffixes =
        [
            "diffuse", "lightmap", "light_map", "shadowramp", "shadow_ramp", "ramp",
            "normalmap", "normal_map", "metalmap", "materialmap", "specular", "roughness",
            "watermark", "logo", "mask", "overlay",
        ];

    /// <summary>Words that make an otherwise unremarkable name look like a preview after all.</summary>
    private static readonly string[] PreviewLikeWords =
        ["preview", "cover", "thumb", "screenshot", "splash", "showcase"];

    /// <summary>How large an image with no preview-like name must be to be offered as a guess.</summary>
    internal const long MinimumUnnamedBytes = 16 * 1024;

    private readonly ILogger _logger = logger.ForContext<ModPreviewSource>();

    /// <inheritdoc />
    public Task<ModPreview?> FindAsync(
        string modDirectory,
        ModConfig? config = null,
        ModScanBounds? bounds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);

        // Synchronous file work, on a worker so the caller's thread stays free.
        return Task.Run(() => Find(modDirectory, config, bounds ?? ModScanBounds.Default, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenAsync(
        string modDirectory, ModConfig? config = null, CancellationToken cancellationToken = default)
    {
        var preview = await FindAsync(modDirectory, config, bounds: null, cancellationToken)
            .ConfigureAwait(false);

        if (preview is null)
        {
            return null;
        }

        try
        {
            return File.OpenRead(preview.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not read the preview image at {Path}", preview.Path);
            return null;
        }
    }

    /// <summary>Whether a file name is a texture or a signature rather than a preview.</summary>
    /// <param name="stem">The file name without its extension.</param>
    /// <returns><c>true</c> when the file should never be offered as a preview.</returns>
    internal static bool IsNotAPreview(string stem)
    {
        foreach (var suffix in NotAPreviewSuffixes)
        {
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Ranks a candidate file name. Lower is a better match.</summary>
    /// <param name="stem">The file name without its extension.</param>
    /// <returns>How preview-like the name is.</returns>
    internal static ModPreviewMatch RankName(string stem)
    {
        if (stem.Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            return ModPreviewMatch.NamedExactly;
        }

        if (stem.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
        {
            return ModPreviewMatch.NamedPrefix;
        }

        if (stem.StartsWith("prev", StringComparison.OrdinalIgnoreCase))
        {
            return ModPreviewMatch.NamedAbbreviated;
        }

        foreach (var word in PreviewLikeWords)
        {
            if (stem.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return ModPreviewMatch.NamedLike;
            }
        }

        return ModPreviewMatch.Unnamed;
    }

    private ModPreview? Find(
        string modDirectory, ModConfig? config, ModScanBounds bounds, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(modDirectory))
        {
            return null;
        }

        // The user chose no picture; a search would find the author's again.
        if (config?.NoImage == true)
        {
            return null;
        }

        if (Declared(modDirectory, config) is { } declared)
        {
            return declared;
        }

        var state = new Search(modDirectory, bounds);
        Walk(state, modDirectory, depth: 0, cancellationToken);
        return state.Best;
    }

    /// <summary>Resolves the declared <c>imagePath</c>, casing-tolerantly, if it names a real file.</summary>
    private ModPreview? Declared(string modDirectory, ModConfig? config)
    {
        if (config?.ImagePath is not { Length: > 0 } declared)
        {
            return null;
        }

        // Inside this mod's folder or not at all: a mod.json from an archive names whatever its author liked.
        if (!UntrustedLocation.TryResolveInside(modDirectory, declared, out _))
        {
            _logger.Warning(
                "{Mod} declares a preview image at {Declared}, outside the mod; searching the folder instead",
                modDirectory, declared);

            return null;
        }

        var joined = Combine(modDirectory, declared);

        if (!PathComparer.TryResolveExisting(joined, out var resolved) || !File.Exists(resolved))
        {
            _logger.Warning(
                "{Mod} declares a preview image at {Declared}, which is not there; searching the folder instead",
                modDirectory, declared);

            return null;
        }

        if (!UntrustedLocation.IsSameOrUnderExactly(
                DriveInfoVolumeResolver.ResolveLinks(Path.GetFullPath(modDirectory)),
                DriveInfoVolumeResolver.ResolveLinks(Path.GetFullPath(resolved))))
        {
            _logger.Warning(
                "{Mod} declares a preview image at {Declared}, which a link leads out of the mod; searching the folder instead",
                modDirectory, declared);

            return null;
        }

        return Describe(modDirectory, resolved, ModPreviewMatch.Declared);
    }

    /// <summary>Joins a mod-relative path, which may carry more than one segment.</summary>
    private static string Combine(string directory, string relative)
    {
        var current = directory;

        foreach (var segment in PathComparer.Normalize(relative).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = PathComparer.Join(current, segment);
        }

        return current;
    }

    private static ModPreview? Describe(string modDirectory, string path, ModPreviewMatch match)
    {
        try
        {
            var relative = PathComparer.TryGetRelativePath(modDirectory, path) ?? Path.GetFileName(path);
            return new ModPreview(path, relative, match, new FileInfo(path).LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Walk(Search state, string directory, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string[] entries;

        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not look inside {Directory} for a preview image", directory);
            return;
        }

        // Ordinal, so two scans of one folder choose the same file.
        Array.Sort(entries, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(entry))
            {
                var name = Path.GetFileName(entry);

                if (name.Equals(ModConfigSchema.DirectoryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A link can point back up the tree: never followed.
                if (new DirectoryInfo(entry).LinkTarget is not null || depth + 1 > state.Bounds.MaxDepth)
                {
                    continue;
                }

                Walk(state, entry, depth + 1, cancellationToken);
                continue;
            }

            if (state.FilesSeen >= state.Bounds.MaxFiles)
            {
                return;
            }

            state.FilesSeen++;
            Consider(state, entry, depth);
        }
    }

    private static void Consider(Search state, string path, int depth)
    {
        var extension = Path.GetExtension(path);

        if (!IModPreviewEditor.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(path);

        if (IsNotAPreview(stem))
        {
            return;
        }

        var match = RankName(stem);

        // By name confidence first, then depth, then name: a wrapper folder's preview.png beats a stray top-level
        // texture.
        var key = (match, depth, PathComparer.Normalize(path));

        if (state.BestKey is { } best && key.CompareTo(best) >= 0)
        {
            return;
        }

        if (match is ModPreviewMatch.Unnamed)
        {
            try
            {
                if (new FileInfo(path).Length < MinimumUnnamedBytes)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        if (Describe(state.Root, path, match) is not { } preview)
        {
            return;
        }

        state.Best = preview;
        state.BestKey = key;
    }

    private sealed class Search(string root, ModScanBounds bounds)
    {
        public string Root { get; } = root;

        public ModScanBounds Bounds { get; } = bounds;

        public int FilesSeen { get; set; }

        public ModPreview? Best { get; set; }

        public (ModPreviewMatch Match, int Depth, string Path)? BestKey { get; set; }
    }
}
