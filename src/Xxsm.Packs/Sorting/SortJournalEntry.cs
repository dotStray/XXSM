using System.Text.Json.Serialization;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Sorting;

/// <summary>What a journal line records.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<SortJournalEntryKind>))]
public enum SortJournalEntryKind
{
    /// <summary>A mod was moved by a sort run.</summary>
    Move,

    /// <summary>A move was put back by an undo.</summary>
    Undo,
}

/// <summary>One line of <c>&lt;Mods&gt;/.xxsm/sort-log.jsonl</c>; paths are relative to the Mods folder.</summary>
public sealed record SortJournalEntry
{
    /// <summary>The run this line belongs to. One id per <c>--apply</c>, shared by every move in it.</summary>
    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    /// <summary>When it happened.</summary>
    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>Whether this line is a move or the undoing of one.</summary>
    [JsonPropertyName("kind")]
    public required SortJournalEntryKind Kind { get; init; }

    /// <summary>Where the mod was, relative to the Mods folder.</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Where the mod went, relative to the Mods folder.</summary>
    [JsonPropertyName("to")]
    public required string To { get; init; }

    /// <summary>For an undo line, the run whose move this reverses. Null on a move line.</summary>
    [JsonPropertyName("undoes")]
    public string? Undoes { get; init; }

    /// <summary>The character folder this move created, relative to the Mods folder, or null if it existed.</summary>
    /// <remarks>Undo removes these folders and no others. Set on the first move into each.</remarks>
    [JsonPropertyName("createdFolder")]
    public string? CreatedFolder { get; init; }

    /// <summary>The decision behind the move, with its root path made relative to the Mods folder.</summary>
    [JsonPropertyName("decision")]
    public SortDecision? Decision { get; init; }
}
