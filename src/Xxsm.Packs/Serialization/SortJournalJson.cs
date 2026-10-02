using System.Text.Json.Serialization;
using Xxsm.Packs.Sorting;

namespace Xxsm.Packs.Serialization;

/// <summary>The source-generated context for <see cref="SortJournalEntry"/> and what it carries.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SortJournalEntry))]
public sealed partial class SortJournalJsonContext : JsonSerializerContext;
