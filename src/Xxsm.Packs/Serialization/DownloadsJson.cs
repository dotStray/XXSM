using System.Text.Json.Serialization;
using Xxsm.Packs.Downloads;

namespace Xxsm.Packs.Serialization;

/// <summary>The source-generated context for the download list.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(DownloadFile))]
public sealed partial class DownloadsJsonContext : JsonSerializerContext;
