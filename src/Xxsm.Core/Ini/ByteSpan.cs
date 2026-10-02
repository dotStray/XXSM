namespace Xxsm.Core.Ini;

/// <summary>A range of bytes in the original file, pointing at a line, key or value without copying it.</summary>
/// <param name="Start">Offset of the first byte, from the start of the file.</param>
/// <param name="Length">How many bytes. Zero means an insertion point.</param>
public readonly record struct ByteSpan(int Start, int Length)
{
    /// <summary>Offset one past the last byte.</summary>
    public int End => Start + Length;

    /// <summary>Whether the span covers no bytes at all.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Takes this span out of <paramref name="bytes"/>.</summary>
    /// <param name="bytes">The buffer the span refers to.</param>
    /// <returns>The bytes this span covers.</returns>
    public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes) => bytes.Slice(Start, Length);

    /// <inheritdoc />
    public override string ToString() => $"[{Start}..{End})";
}
