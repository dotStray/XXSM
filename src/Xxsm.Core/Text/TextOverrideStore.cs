using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Text;

/// <summary>The default <see cref="ITextOverrideStore"/>: two files in the configuration directory.</summary>
public sealed class TextOverrideStore(IAppPaths paths, ILogger logger, TextEditing editing) : ITextOverrideStore
{
    private const string OverridesFileName = "text.json";
    private const string ReferenceFileName = "text.reference.json";

    /// <summary>Comments and trailing commas are allowed: a person types into this file.</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IAppPaths _paths = paths;
    private readonly ILogger _logger = logger.ForContext<TextOverrideStore>();

    /// <inheritdoc />
    public bool IsEnabled => editing.IsEnabled;

    /// <inheritdoc />
    public string OverridesPath => _paths.TextFile;

    /// <inheritdoc />
    public string ReferencePath => _paths.TextReferenceFile;

    /// <inheritdoc />
    public TextOverrides Read()
    {
        var path = OverridesPath;

        if (!IsEnabled || !File.Exists(path))
        {
            return TextOverrides.None(path);
        }

        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not read interface text from {Path}", path);

            return new TextOverrides(
                path,
                Exists: true,
                new Dictionary<string, string>(StringComparer.Ordinal),
                $"'{PathDisplay.Show(path)}' could not be read: {ex.Message}");
        }

        return Parse(path, json);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>?> ReadReferenceAsync(
        CancellationToken cancellationToken = default)
    {
        var path = ReferencePath;

        if (!IsEnabled || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var parsed = Parse(path, json);

            return parsed.Problem is null ? parsed.Values : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not read the interface-text reference at {Path}", path);

            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteReferenceAsync(
        IReadOnlyDictionary<string, string> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (!IsEnabled)
        {
            return;
        }

        var body = Compose(
            entries,
            commented: false,
            header:
            [
                "Every piece of text XXSM can show, with its built-in wording.",
                "",
                "This file is rewritten every time XXSM starts, so editing it achieves",
                $"nothing. To change what the application says, edit {OverridesFileName}",
                "in this same folder.",
            ]);

        await WriteAsync(ReferencePath, body, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CreateOverridesAsync(
        IReadOnlyDictionary<string, string> entries,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (!IsEnabled)
        {
            throw new InvalidOperationException("This build does not read text.json.");
        }

        var path = OverridesPath;

        // An existing file holds edits that exist nowhere else; replacing one takes an explicit ask.
        if (!overwrite && File.Exists(path))
        {
            return false;
        }

        var body = Compose(
            entries,
            commented: true,
            header:
            [
                "XXSM interface text.",
                "",
                "Every line below is switched off. To change what the application says,",
                "delete the two slashes at the start of a line and edit the text on the",
                "right of the colon. Leave the name on the left alone.",
                "",
                "Restart XXSM to see the change.",
                "",
                $"Anything you do not list here keeps its built-in wording. {ReferenceFileName}",
                "in this same folder is regenerated on every launch and always lists every",
                "name a new version has added.",
            ]);

        await WriteAsync(path, body, cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Turns the file's text into replacements, or into a reason it could not be used.</summary>
    private static TextOverrides Parse(string path, string json)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(json, ReadOptions);

            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return new TextOverrides(
                    path,
                    Exists: true,
                    values,
                    $"'{PathDisplay.Show(path)}' has to hold a list of name: text pairs inside {{ }}. " +
                    "The built-in wording is being used instead.");
            }

            var skipped = new List<string>();

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                if (property.Value.ValueKind is not JsonValueKind.String)
                {
                    skipped.Add(property.Name);
                    continue;
                }

                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }

            var problem = skipped.Count is 0
                ? null
                : string.Format(
                    CultureInfo.CurrentCulture,
                    "In '{0}', {1} must be in quotes: {2}. {3}",
                    path,
                    skipped.Count is 1 ? "this entry" : "these entries",
                    string.Join(", ", skipped),
                    "They are being ignored; everything else in the file was applied.");

            return new TextOverrides(path, Exists: true, values, problem);
        }
        catch (JsonException ex)
        {
            return new TextOverrides(
                path,
                Exists: true,
                new Dictionary<string, string>(StringComparer.Ordinal),
                $"'{PathDisplay.Show(path)}' could not be understood: {ex.Message} " +
                "The built-in wording is being used until it is fixed.");
        }
    }

    /// <summary>Lays out the entries as JSON, sorted by name, with a blank line between key groups.</summary>
    private static string Compose(
        IReadOnlyDictionary<string, string> entries,
        bool commented,
        IReadOnlyList<string> header)
    {
        var builder = new StringBuilder();

        builder.Append('{').Append('\n');

        foreach (var line in header)
        {
            builder.Append(line.Length is 0 ? "  //" : "  // ").Append(line).Append('\n');
        }

        var ordered = entries.Keys.Order(StringComparer.Ordinal).ToList();
        string? previousGroup = null;

        foreach (var key in ordered)
        {
            var group = GroupOf(key);

            if (!string.Equals(group, previousGroup, StringComparison.Ordinal))
            {
                builder.Append('\n');
                previousGroup = group;
            }

            builder
                .Append("  ")
                .Append(commented ? "// " : string.Empty)
                .Append(Quote(key))
                .Append(": ")
                .Append(Quote(entries[key]))
                .Append(',')
                .Append('\n');
        }

        // The trailing comma on the last entry is deliberate and legal: any line can be uncommented alone.
        builder.Append('}').Append('\n');

        return builder.ToString();
    }

    /// <summary>The part of a key before its first underscore, which names its area.</summary>
    private static string GroupOf(string key)
    {
        var underscore = key.IndexOf('_', StringComparison.Ordinal);

        return underscore > 0 ? key[..underscore] : key;
    }

    /// <summary>Escapes a string and wraps it in quotes, keeping characters such as an em-dash as they are.</summary>
    private static string Quote(string value) =>
        $"\"{JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";

    /// <summary>Writes a file through a temporary, so a failure never truncates the old one.</summary>
    private async Task WriteAsync(string path, string body, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.ConfigDirectory);

            await AtomicFile.WriteAllTextAsync(path, body, AtomicWriteOptions.DurableOnly, cancellationToken)
                .ConfigureAwait(false);

            _logger.Information("Wrote interface text to {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModOperationException(
                $"Could not write '{PathDisplay.Show(path)}': {ex.Message}",
                path,
                ex);
        }
    }
}
