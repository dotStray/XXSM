using System.Text.Json.Serialization;

namespace Xxsm.Packs.Model;

/// <summary>Which 3DMigoto field a hash came from, which decides its weight in scoring.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HashKind>))]
public enum HashKind
{
    /// <summary>A hash whose field is unknown, such as one pasted by hand. Scores as <see cref="DrawVb"/>.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    /// <summary>Index buffer: the most discriminating field, never shared between characters. Weight 10.</summary>
    [JsonStringEnumMemberName("ib")]
    Ib,

    /// <summary>Position vertex buffer. Weight 8.</summary>
    [JsonStringEnumMemberName("position_vb")]
    PositionVb,

    /// <summary>Blend vertex buffer. Weight 8.</summary>
    [JsonStringEnumMemberName("blend_vb")]
    BlendVb,

    /// <summary>Texture-coordinate vertex buffer. Weight 6.</summary>
    [JsonStringEnumMemberName("texcoord_vb")]
    TexcoordVb,

    /// <summary>Draw vertex buffer. Weight 6.</summary>
    [JsonStringEnumMemberName("draw_vb")]
    DrawVb,

    /// <summary>Vertex shader: shared by every character, not an identity. Weight 0, and never indexed.</summary>
    [JsonStringEnumMemberName("root_vs")]
    RootVs,

    /// <summary>A texture. Weak — one Zenless value spans 87 of 88 characters. Weight 1.</summary>
    [JsonStringEnumMemberName("texture")]
    Texture,
}
