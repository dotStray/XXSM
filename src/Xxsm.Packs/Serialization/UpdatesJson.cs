using System.Text.Json.Serialization;
using Xxsm.Packs.Updates;

namespace Xxsm.Packs.Serialization;

/// <summary>The source-generated context for GitHub's release description.</summary>
[JsonSerializable(typeof(GitHubRelease))]
public sealed partial class UpdatesJsonContext : JsonSerializerContext;
