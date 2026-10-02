using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Xxsm.Core.Settings;

namespace Xxsm.Cli.Commands;

/// <summary>The <c>--mods</c> / <c>--game</c> pair: the folder given, else the one saved for the game.</summary>
internal static class ModsFolderOptions
{
    /// <summary>The Mods folder to act on.</summary>
    public static Option<string?> Mods() => new("--mods")
    {
        Description =
            "The game's Mods folder. Defaults to the one saved for --game " +
            "(see: xxsm config set --help).",
    };

    /// <summary>Which game's saved Mods folder to fall back to.</summary>
    public static Option<string?> Game() => new("--game")
    {
        Description = "Which game's saved Mods folder to use when --mods is not given.",
    };

    /// <summary>Resolves the Mods folder to act on.</summary>
    /// <exception cref="SettingsLoadException">Neither was given and none is saved.</exception>
    public static Task<string> ResolveAsync(
        ServiceProvider provider,
        ParseResult parse,
        Option<string?> mods,
        Option<string?> game,
        CancellationToken cancellationToken) =>
        provider.GetRequiredService<IAppSettingsStore>().ResolveModsDirectoryAsync(
            parse.GetValue(game), parse.GetValue(mods), cancellationToken);

    /// <summary>Resolves the Mods folder when the game is already known from a required option.</summary>
    public static Task<string> ResolveAsync(
        ServiceProvider provider,
        ParseResult parse,
        Option<string?> mods,
        string gameId,
        CancellationToken cancellationToken) =>
        provider.GetRequiredService<IAppSettingsStore>().ResolveModsDirectoryAsync(
            gameId, parse.GetValue(mods), cancellationToken);
}
