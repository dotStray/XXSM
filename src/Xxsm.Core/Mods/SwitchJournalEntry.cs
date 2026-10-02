using System.Text.Json.Serialization;

namespace Xxsm.Core.Mods;

/// <summary>What a switch journal line records.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SwitchJournalEntryKind>))]
public enum SwitchJournalEntryKind
{
    /// <summary>A mod was switched on or off by a run.</summary>
    [JsonStringEnumMemberName("switch")]
    Switch,

    /// <summary>A switch was put back by an undo.</summary>
    [JsonStringEnumMemberName("undo")]
    Undo,
}

/// <summary>One line of <c>.xxsm/switch-log.jsonl</c>: a mod switched on or off, or undone.</summary>
public sealed record SwitchJournalEntry
{
    /// <summary>The run this line belongs to, shared by every switch in it.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>When it happened.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether this line is a switch or the undoing of one.</summary>
    [JsonPropertyName("kind")]
    public required SwitchJournalEntryKind Kind { get; init; }

    /// <summary>What asked for the run. Recorded on switch lines.</summary>
    [JsonPropertyName("source")]
    public ModSwitchSource? Source { get; init; }

    /// <summary>The profile's name at the time, for a profile's run.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Where the mod was, relative to the Mods folder.</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Where the mod went, relative to the Mods folder.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>For an undo line, the run whose switch this reverses.</summary>
    [JsonPropertyName("undoes")]
    public string? Undoes { get; init; }
}
