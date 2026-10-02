namespace Xxsm.Core.Diagnostics;

/// <summary>How much a diagnostic matters, on one scale for every source.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Worth knowing, changes nothing. A variant with no portrait.</summary>
    Info = 0,

    /// <summary>It loads and works, but is probably not what its author meant. Never blocks loading.</summary>
    Warning,

    /// <summary>Part of it did not load and was dropped. Blocks a Pack Studio export.</summary>
    Error,
}
