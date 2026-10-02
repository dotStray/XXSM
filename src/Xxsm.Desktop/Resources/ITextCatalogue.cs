namespace Xxsm.Desktop.Resources;

/// <summary>Every word the interface can show: <c>Strings.resx</c>, with the user's <c>text.json</c> over it.</summary>
public interface ITextCatalogue
{
    /// <summary>The prefix the interface's words are published under in the application's resources.</summary>
    const string ResourceKeyPrefix = "Text.";

    /// <summary>The wording for a key.</summary>
    /// <param name="key">A name from <c>Strings.resx</c>; use <c>nameof(Strings.X)</c>.</param>
    string this[string key] { get; }

    /// <summary>The built-in wording for every key, before any replacement.</summary>
    IReadOnlyDictionary<string, string> BuiltIn { get; }

    /// <summary>Where the editable file is, whether or not it exists.</summary>
    string OverridesPath { get; }

    /// <summary>Whether there is a file there.</summary>
    bool OverridesExist { get; }

    /// <summary>The keys the file actually replaced, in order.</summary>
    IReadOnlyList<string> ReplacedKeys { get; }

    /// <summary>Names in the file that match nothing this version has. Usually a typo.</summary>
    IReadOnlyList<string> UnknownKeys { get; }

    /// <summary>Why the file could not be used, in the parser's words, or null; it never stops the app.</summary>
    string? Problem { get; }

    /// <summary>Fills in a key whose wording carries placeholders; an unsafe replacement was refused at load.</summary>
    /// <param name="key">A name from <c>Strings.resx</c>; use <c>nameof(Strings.X)</c>.</param>
    /// <param name="arguments">The values for <c>{0}</c>, <c>{1}</c> and so on.</param>
    string Format(string key, params object?[] arguments);
}
