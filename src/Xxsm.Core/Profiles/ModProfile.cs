using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xxsm.Core.Profiles;

/// <summary>A saved set of switched-on mods, in <c>&lt;Mods&gt;/.xxsm/profiles/&lt;id&gt;.json</c>.</summary>
/// <remarks>Applying it switches those mods on and every other off. Unknown keys are kept on rewrite.</remarks>
public sealed record ModProfile
{
    private readonly string _id = string.Empty;
    private readonly string _name = string.Empty;
    private readonly IReadOnlyList<ModProfileEntry> _enabled = [];

    /// <summary>The current format version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Creates an empty profile.</summary>
    [JsonConstructor]
    public ModProfile()
    {
    }

    /// <summary>The format version of this file; 0, when missing, reads as version 1.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>The profile's own id, which is also its file name.</summary>
    /// <remarks>Coerced in the accessor: a file without the key reads as empty, not null.</remarks>
    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        init => _id = value ?? string.Empty;
    }

    /// <summary>What the user called it; coerced like <see cref="Id"/>.</summary>
    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>Whether it is protected from being saved over, renamed or deleted. It can still be applied.</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; init; }

    /// <summary>When it was first saved.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When its list of mods was last saved.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The mods that were switched on when it was saved; coerced like <see cref="Id"/>.</summary>
    [JsonPropertyName("enabled")]
    public IReadOnlyList<ModProfileEntry> Enabled
    {
        get => _enabled;
        init => _enabled = value ?? [];
    }

    /// <summary>Every key XXSM did not recognise, kept verbatim so a rewrite never drops one.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>One switched-on mod in a profile, found again by its id, else by its path.</summary>
public sealed record ModProfileEntry
{
    private readonly string _path = string.Empty;

    /// <summary>The mod's stable id, or <c>null</c> when it had none that could be written.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Where the mod was, relative to Mods, without <c>DISABLED_</c>; coerced like the profile's id.</summary>
    [JsonPropertyName("path")]
    public string Path
    {
        get => _path;
        init => _path = value ?? string.Empty;
    }

    /// <summary>What the mod was called then, for a report that it has gone.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>When the mod was added by itself rather than saved with the rest; null otherwise.</summary>
    [JsonPropertyName("addedAt")]
    public DateTimeOffset? AddedAt { get; init; }
}
