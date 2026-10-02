using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Resources;
using System.Text;
using Avalonia.Controls;
using Serilog;
using Xxsm.Core.Text;

namespace Xxsm.Desktop.Resources;

/// <summary>The default <see cref="ITextCatalogue"/>, read once at start-up; a change needs a restart.</summary>
public sealed class TextCatalogue : ITextCatalogue
{
    /// <summary>The wording as shipped: static, but immutable and read from a compiled resource.</summary>
    private static readonly FrozenDictionary<string, string> Shipped = ReadResx();

    private readonly ConcurrentDictionary<string, CompositeFormat> _formats = new();
    private readonly FrozenDictionary<string, string> _effective;

    /// <summary>Creates the catalogue and reads the user's file.</summary>
    public TextCatalogue(ITextOverrideStore overrides, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(logger);

        var log = logger.ForContext<TextCatalogue>();
        var file = overrides.Read();

        OverridesPath = file.Path;
        OverridesExist = file.Exists;

        var effective = new Dictionary<string, string>(Shipped, StringComparer.Ordinal);
        var replaced = new List<string>();
        var unknown = new List<string>();
        var refused = new List<string>();

        foreach (var (key, value) in file.Values)
        {
            if (!Shipped.TryGetValue(key, out var shipped))
            {
                unknown.Add(key);
                continue;
            }

            if (!PlaceholdersAreSafe(shipped, value))
            {
                refused.Add(key);
                continue;
            }

            effective[key] = value;
            replaced.Add(key);
        }

        replaced.Sort(StringComparer.Ordinal);
        unknown.Sort(StringComparer.Ordinal);
        refused.Sort(StringComparer.Ordinal);

        _effective = effective.ToFrozenDictionary(StringComparer.Ordinal);

        ReplacedKeys = replaced;
        UnknownKeys = unknown;
        Problem = Describe(file.Problem, refused);

        if (Problem is not null)
        {
            log.Warning("Interface text from {Path} was not fully usable: {Problem}", file.Path, Problem);
        }
        else if (replaced.Count > 0)
        {
            log.Information(
                "Interface text: {Count} entries replaced from {Path}", replaced.Count, file.Path);
        }
    }

    /// <inheritdoc />
    public string this[string key]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            // A key with no wording shows as itself, an obviously wrong label rather than an empty one.
            return _effective.TryGetValue(key, out var value) ? value : key;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> BuiltIn => Shipped;

    /// <inheritdoc />
    public string OverridesPath { get; }

    /// <inheritdoc />
    public bool OverridesExist { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> ReplacedKeys { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> UnknownKeys { get; }

    /// <inheritdoc />
    public string? Problem { get; }

    /// <inheritdoc />
    public string Format(string key, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(arguments);

        var format = _formats.GetOrAdd(key, static (k, self) => CompositeFormat.Parse(self[k]), this);

        return string.Format(CultureInfo.CurrentCulture, format, arguments);
    }

    /// <summary>Publishes the shipped wording into a resource dictionary, reading no file.</summary>
    /// <param name="resources">The dictionary to fill, normally the application's.</param>
    public static void PublishShipped(IResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var (key, value) in Shipped)
        {
            resources[ITextCatalogue.ResourceKeyPrefix + key] = value;
        }
    }

    /// <summary>Publishes the effective wording, replacements included, over whatever is there.</summary>
    /// <param name="resources">The dictionary to fill, normally the application's.</param>
    public void PublishTo(IResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        foreach (var (key, value) in _effective)
        {
            resources[ITextCatalogue.ResourceKeyPrefix + key] = value;
        }
    }

    /// <summary>Whether a replacement's braces balance and ask for no value the shipped wording lacks.</summary>
    private static bool PlaceholdersAreSafe(string shipped, string replacement)
    {
        try
        {
            return CompositeFormat.Parse(replacement).MinimumArgumentCount
                   <= CompositeFormat.Parse(shipped).MinimumArgumentCount;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Combines the file's own complaint with the entries this class refused.</summary>
    private static string? Describe(string? fileProblem, List<string> refused)
    {
        if (refused.Count is 0)
        {
            return fileProblem;
        }

        var refusal = string.Format(
            CultureInfo.CurrentCulture,
            "{0} {1} kept the built-in wording: the replacement uses a {{number}} that XXSM does not fill in there.",
            string.Join(", ", refused),
            refused.Count is 1 ? "was" : "were");

        return fileProblem is null ? refusal : fileProblem + " " + refusal;
    }

    /// <summary>Reads every entry out of the compiled resource file.</summary>
    private static FrozenDictionary<string, string> ReadResx()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        using var set = Strings.ResourceManager.GetResourceSet(
            CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true)
            ?? throw new MissingManifestResourceException(
                "Strings.resx is not embedded in Xxsm.Desktop. The interface would have no words in it.");

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                entries[key] = value;
            }
        }

        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
