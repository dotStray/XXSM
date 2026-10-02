using System.Text.Json;
using Xxsm.Core.Diagnostics;
using Xxsm.Core.Io;
using Xxsm.Core.Serialization;

namespace Xxsm.Core.Hashes;

/// <summary>Reads an upstream <c>hash.json</c> without ever throwing.</summary>
public static class UpstreamHashReader
{
    /// <summary>Reads the components from a file's bytes.</summary>
    /// <param name="bytes">The file contents. May be empty or malformed.</param>
    /// <param name="path">The path, for the diagnostic message.</param>
    /// <returns>The components, and anything that went wrong reading them.</returns>
    public static UpstreamHashResult Read(ReadOnlySpan<byte> bytes, string? path = null)
    {
        if (bytes.IsEmpty)
        {
            return new UpstreamHashResult([], [
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    UpstreamHashDiagnosticCodes.Empty,
                    $"{Describe(path)} is empty."),
            ]);
        }

        try
        {
            var components = JsonSerializer.Deserialize(
                bytes, CoreJsonContext.Default.IReadOnlyListUpstreamHashComponent);

            return components is null
                ? new UpstreamHashResult([], [
                    new Diagnostic(
                        DiagnosticSeverity.Warning,
                        UpstreamHashDiagnosticCodes.NotAnArray,
                        $"{Describe(path)} contains the JSON value null, not a list of components."),
                ])
                : new UpstreamHashResult(components, []);
        }
        catch (JsonException exception)
        {
            return new UpstreamHashResult([], [
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    UpstreamHashDiagnosticCodes.Malformed,
                    $"{Describe(path)} is not valid JSON and was ignored: {exception.Message}"),
            ]);
        }
        catch (NotSupportedException exception)
        {
            return new UpstreamHashResult([], [
                new Diagnostic(
                    DiagnosticSeverity.Warning,
                    UpstreamHashDiagnosticCodes.Malformed,
                    $"{Describe(path)} is JSON but not in the shape a hash.json should be, "
                    + $"and was ignored: {exception.Message}"),
            ]);
        }
    }

    private static string Describe(string? path) => path is null ? "This hash.json" : $"The hash.json at {PathDisplay.Show(path)}";
}

/// <summary>What <see cref="UpstreamHashReader.Read"/> found.</summary>
/// <param name="Components">The components, empty when the file could not be read.</param>
/// <param name="Diagnostics">Anything that went wrong. Empty for a good file.</param>
public sealed record UpstreamHashResult(
    IReadOnlyList<UpstreamHashComponent> Components,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Every well-formed hash across every component, lowercase and deduplicated, in a stable order.</summary>
    public IReadOnlyList<string> Hashes()
    {
        var found = new List<string>();
        foreach (var hash in Components.SelectMany(component => component.Hashes()))
        {
            if (!found.Contains(hash, StringComparer.Ordinal))
            {
                found.Add(hash);
            }
        }

        return found;
    }
}

/// <summary>Stable codes for problems found in an upstream <c>hash.json</c>.</summary>
public static class UpstreamHashDiagnosticCodes
{
    /// <summary>The file was empty.</summary>
    public const string Empty = "hashjson.empty";

    /// <summary>The file was not valid JSON, or not the shape a hash.json should be.</summary>
    public const string Malformed = "hashjson.malformed";

    /// <summary>The file parsed but held the JSON value null rather than a list.</summary>
    public const string NotAnArray = "hashjson.not-an-array";
}
