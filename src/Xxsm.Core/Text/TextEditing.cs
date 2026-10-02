namespace Xxsm.Core.Text;

/// <summary>Whether this build lets the interface's wording be changed in <c>text.json</c>.</summary>
/// <param name="IsEnabled">True to read and offer the file; false to show the built-in wording only.</param>
public sealed record TextEditing(bool IsEnabled)
{
    /// <summary>On in a Debug build, off in a Release build.</summary>
    public static TextEditing ForThisBuild { get; } = new(
#if DEBUG
        true
#else
        false
#endif
    );
}
