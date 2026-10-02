namespace Xxsm.Core.Settings;

/// <summary>The settings could not be read, or a setting the caller needed is not set.</summary>
public sealed class SettingsLoadException : XxsmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Text written to be shown to a user unedited.</param>
    /// <param name="path">The settings file involved, when there is one.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public SettingsLoadException(string message, string? path = null, Exception? innerException = null)
        : base(message, innerException) => Path = path;

    /// <summary>The settings file involved, or null when the failure is not about a file.</summary>
    public string? Path { get; }
}
