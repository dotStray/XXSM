namespace Xxsm.Core.Diagnostics;

/// <summary>One problem found in a file XXSM read and partly understood.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Code">A stable machine-readable code.</param>
/// <param name="Message">Plain language, shown to the user unedited.</param>
/// <param name="Line">The 1-based line it was found on, or 0 for the whole file.</param>
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    int Line = 0)
{
    /// <summary>The folders or files it is about, the one to act on first; empty for none.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() =>
        Line > 0
            ? $"[{Severity}] line {Line}: {Message} ({Code})"
            : $"[{Severity}] {Message} ({Code})";
}
