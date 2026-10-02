using System.Text.Json;
using System.Text.Json.Serialization;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Hashes;
using Xxsm.Core.Mods;
using Xxsm.Core.Profiles;
using Xxsm.Core.Settings;

namespace Xxsm.Core.Serialization;

/// <summary>The serialisation context for Core's JSON files: tolerant reading, indented writing.</summary>
/// <remarks>Options set anywhere else are ignored by a generated <c>JsonTypeInfo</c>; they belong here.</remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(IReadOnlyList<UpstreamHashComponent>))]
[JsonSerializable(typeof(ModConfig))]
[JsonSerializable(typeof(GameBananaProfilePage))]
[JsonSerializable(typeof(GameBananaDownloadPage))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ModProfile))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
