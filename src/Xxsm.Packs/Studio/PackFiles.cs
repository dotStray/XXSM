using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Studio;

/// <summary>Renders a draft's pack files: the one place, so a draft and an export are written alike.</summary>
internal static class PackFiles
{
    /// <summary>The five pack files, in a fixed order, each as its exact text.</summary>
    public static List<(string Name, string Json)> Render(PackDraft draft)
    {
        var variants = PackDrafts.Ordered(draft.Variants);
        var context = PackJsonContext.Default;

        return
        [
            (PackSchema.ManifestFile, Serialize(draft.Manifest, context.PackManifest)),
            (PackSchema.GameFile, Serialize(draft.Game, context.GameDefinition)),
            (PackSchema.VariantsFile, Serialize(variants, context.IReadOnlyListPackVariant)),
            (PackSchema.HashesFile, Serialize(PackDrafts.Ordered(draft.Hashes), context.HashIndexFile)),
        ];
    }

    /// <summary>Serialises one file the way every Studio file is written: indented, ending in a line break.</summary>
    /// <typeparam name="T">The file's type.</typeparam>
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo) + "\n";
}
