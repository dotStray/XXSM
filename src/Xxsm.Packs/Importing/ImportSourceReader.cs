using System.Globalization;
using System.Text;
using System.Text.Json;
using Xxsm.Core.Io;

namespace Xxsm.Packs.Importing;

/// <summary>Reads JASM's <c>.JASM_ModConfig.json</c> and XX-Mod-Manager's <c>mod.json</c> into details.</summary>
/// <remarks>A field of the wrong type is skipped, not fatal; placeholders such as "Unknown" are not data.</remarks>
internal static class ImportSourceReader
{
    /// <summary>JASM's file name.</summary>
    public const string JasmFileName = ".JASM_ModConfig.json";

    /// <summary>XX-Mod-Manager's file name.</summary>
    public const string XxModManagerFileName = "mod.json";

    private const string Unknown = "Unknown";
    private const string NoDescription = "no description";

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // JASM writes DateAdded in the writing PC's culture, and globalization is invariant here.
    private static readonly string[] DateFormats =
    [
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy H:mm:ss",
        "d/M/yyyy H:mm:ss",
        "d/M/yyyy h:mm:ss tt",
        "d.M.yyyy H:mm:ss",
        "yyyy/M/d H:mm:ss",
        "yyyy-M-d H:mm:ss",
        "yyyy.M.d H:mm:ss",
        "d-M-yyyy H:mm:ss",
    ];

    /// <summary>Parses a file's text.</summary>
    /// <param name="json">The file's contents.</param>
    /// <param name="document">The parsed document, when it is a JSON object.</param>
    /// <param name="problem">Why it could not be read, otherwise.</param>
    public static bool TryParse(string json, out JsonDocument? document, out string? problem)
    {
        try
        {
            document = JsonDocument.Parse(json, Options);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                document = null;
                problem = "It is not a JSON object.";
                return false;
            }

            problem = null;
            return true;
        }
        catch (JsonException ex)
        {
            document = null;
            problem = $"It is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>Whether a <c>mod.json</c> is XX-Mod-Manager's; a mod may ship a <c>mod.json</c> of its own.</summary>
    public static bool IsXxModManager(JsonElement root) =>
        Get(root, "id") is not null &&
        (root.TryGetProperty("modName", out _) || root.TryGetProperty("character", out _));

    /// <summary>Reads JASM's settings.</summary>
    /// <param name="root">The parsed file.</param>
    /// <param name="modFolder">The mod folder, to check the picture is there.</param>
    public static ImportedDetails ReadJasm(JsonElement root, string modFolder) => new()
    {
        SourceId = Get(root, "Id"),
        Name = Get(root, "CustomName"),
        Author = Get(root, "Author"),
        Version = Get(root, "Version"),
        Description = Get(root, "Description"),
        Url = Url(Get(root, "ModUrl")),
        ImagePath = Picture(Get(root, "ImagePath"), modFolder),
        DateAdded = Date(Get(root, "DateAdded")),
        Character = Get(root, "CharacterSkinOverride"),
    };

    /// <summary>Reads XX-Mod-Manager's details.</summary>
    /// <param name="root">The parsed file.</param>
    /// <param name="modFolder">The mod folder, to check the picture is there.</param>
    public static ImportedDetails ReadXxModManager(JsonElement root, string modFolder) => new()
    {
        SourceId = Get(root, "id"),
        Name = Get(root, "modName"),
        Author = Placeholder(Get(root, "author")),
        Description = Placeholder(Get(root, "description")),
        Url = Url(Get(root, "url")),
        ImagePath = Picture(Placeholder(Get(root, "preview")), modFolder),
        Tags = Strings(root, "tags"),
        Notes = Hotkeys(root),
        Character = Placeholder(Get(root, "character")),
    };

    private static string? Get(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()?.Trim() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string? Placeholder(string? value) =>
        value is null ||
        string.Equals(value, Unknown, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, NoDescription, StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private static string? Url(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;

    /// <summary>A picture path, made relative to the mod folder, when a file of that name is really in it.</summary>
    private static string? Picture(string? value, string modFolder)
    {
        if (value is null)
        {
            return null;
        }

        var normalised = value.Replace('\\', '/');
        var looksAbsolute = normalised.StartsWith('/') || (normalised.Length > 2 && normalised[1] == ':');
        var relative = looksAbsolute ? normalised[(normalised.LastIndexOf('/') + 1)..]
            : normalised.StartsWith("./", StringComparison.Ordinal) ? normalised[2..]
            : normalised;

        var candidate = Path.GetFullPath(Path.Combine(modFolder, relative));

        return UntrustedLocation.IsSameOrUnderExactly(modFolder, candidate) &&
               PathComparer.TryResolveExisting(candidate, out var resolved) &&
               File.Exists(resolved)
            ? PathComparer.TryGetRelativePath(modFolder, resolved)
            : null;
    }

    private static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(item => item.Length > 0),
        ];
    }

    /// <summary>XX-Mod-Manager's hotkey list, one "key — what it does" per line.</summary>
    private static string? Hotkeys(JsonElement root)
    {
        if (!root.TryGetProperty("hotkeys", out var hotkeys) || hotkeys.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = new StringBuilder();

        foreach (var hotkey in hotkeys.EnumerateArray())
        {
            if (hotkey.ValueKind != JsonValueKind.Object || Get(hotkey, "key") is not { } key)
            {
                continue;
            }

            lines.Append(key);

            if (Get(hotkey, "description") is { } description)
            {
                lines.Append(" — ").Append(description);
            }

            lines.Append('\n');
        }

        return lines.Length == 0 ? null : "Hotkeys:\n" + lines.ToString().TrimEnd('\n');
    }

    private static DateTimeOffset? Date(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ||
            DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return parsed;
        }

        return null;
    }
}
