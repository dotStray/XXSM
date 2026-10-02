using System.Text.Json.Serialization;
using Xxsm.Core.Mods;

namespace Xxsm.Core.Serialization;

/// <summary>The serialisation context for <c>.xxsm/switch-log.jsonl</c>: one entry per line, not indented.</summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(SwitchJournalEntry))]
public sealed partial class SwitchJournalJsonContext : JsonSerializerContext;
