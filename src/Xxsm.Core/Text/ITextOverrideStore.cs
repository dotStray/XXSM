namespace Xxsm.Core.Text;

/// <summary>Reads and writes the interface-text files: <c>text.json</c>, the person's, and the reference.</summary>
/// <remarks><c>text.json</c> is written only when asked; <c>text.reference.json</c> is rewritten every
/// launch.</remarks>
public interface ITextOverrideStore
{
    /// <summary>Whether this build reads <c>text.json</c> at all; when false, every read is empty and nothing is
    /// written.</summary>
    bool IsEnabled { get; }

    /// <summary>Where the editable file is. <c>&lt;config&gt;/text.json</c>.</summary>
    string OverridesPath { get; }

    /// <summary>Where the generated key list is. <c>&lt;config&gt;/text.reference.json</c>.</summary>
    string ReferencePath { get; }

    /// <summary>Reads the editable file, synchronously, once at start-up before any window exists.</summary>
    /// <returns>What it said; never throws, a bad file giving the reason in <see
    /// cref="TextOverrides.Problem"/>.</returns>
    TextOverrides Read();

    /// <summary>Reads the generated key list back, for anything without the interface's resources.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Every key with its built-in wording, or null when the file is missing or unreadable.</returns>
    Task<IReadOnlyDictionary<string, string>?> ReadReferenceAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Writes the generated key list, replacing whatever was there.</summary>
    /// <param name="entries">Every key the application has, with its built-in wording.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task WriteReferenceAsync(
        IReadOnlyDictionary<string, string> entries,
        CancellationToken cancellationToken = default);

    /// <summary>Creates <c>text.json</c> with every key present but commented out.</summary>
    /// <param name="entries">Every key the application has, with its built-in wording.</param>
    /// <param name="overwrite">Whether to replace a file that is already there.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> if a file was written, <see langword="false"/> if one already existed.</returns>
    /// <exception cref="InvalidOperationException">This build does not read the file (<see cref="IsEnabled"/>).</exception>
    Task<bool> CreateOverridesAsync(
        IReadOnlyDictionary<string, string> entries,
        bool overwrite = false,
        CancellationToken cancellationToken = default);
}
