using System.Text.Json;
using System.Text.Json.Serialization;
using Xxsm.Packs.Characters;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Merge;
using Xxsm.Packs.Model;
using Xxsm.Packs.Registry;
using Xxsm.Packs.Studio;

namespace Xxsm.Packs.Serialization;

/// <summary>The source-generated serialisation context for every pack, overlay and registry file.</summary>
/// <remarks>Options passed beside a generated type info are ignored: the settings belong here.</remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PackManifest))]
[JsonSerializable(typeof(GameDefinition))]
[JsonSerializable(typeof(IReadOnlyList<PackVariant>))]
[JsonSerializable(typeof(HashIndexFile))]
[JsonSerializable(typeof(PackOverlay))]
[JsonSerializable(typeof(AttributeValue))]
[JsonSerializable(typeof(RegistryIndex))]
[JsonSerializable(typeof(PackPreferences))]
[JsonSerializable(typeof(CharacterDeletion))]
[JsonSerializable(typeof(StudioDraftInfo))]
[JsonSerializable(typeof(SkippedUpdateRecord))]
[JsonSerializable(typeof(PackChanges))]
[JsonSerializable(typeof(PackRemoval))]
public sealed partial class PackJsonContext : JsonSerializerContext;
