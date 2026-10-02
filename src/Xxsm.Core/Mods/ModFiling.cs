using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModFiling"/>.</summary>
public sealed class ModFiling(IModConfigStore configs, IModFileOperations files, ILogger logger) : IModFiling
{
    private readonly IModConfigStore _configs = configs;
    private readonly IModFileOperations _files = files;
    private readonly ILogger _logger = logger.ForContext<ModFiling>();

    /// <inheritdoc />
    public async Task RememberAsync(
        string modFolder, string variantInternalName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantInternalName);

        await _configs
            .UpdateAsync(modFolder, config => config with { VariantOverride = variantInternalName }, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information("Remembered {Mod} as filed by hand under {Variant}", modFolder, variantInternalName);
    }

    /// <inheritdoc />
    public async Task<bool> ForgetAsync(string modFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        // A mistyped path must not read as "nobody filed it", which looks like success.
        if (!PathComparer.TryResolveExisting(modFolder, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException($"There is no mod folder at '{PathDisplay.Show(modFolder)}'.", modFolder);
        }

        var existing = await _configs.ReadAsync(resolved, cancellationToken).ConfigureAwait(false);

        if (existing?.VariantOverride is not { Length: > 0 } was)
        {
            return false;
        }

        await _configs
            .UpdateAsync(resolved, config => config with { VariantOverride = null }, cancellationToken)
            .ConfigureAwait(false);

        _logger.Information("Forgot that {Mod} was filed by hand under {Variant}", modFolder, was);
        return true;
    }

    /// <inheritdoc />
    public async Task<ModFilingResult> MoveAsync(
        string modFolder,
        string destinationParent,
        string variantInternalName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantInternalName);

        var previous = await PreviousFilingAsync(modFolder, cancellationToken).ConfigureAwait(false);

        var moved = await _files
            .MoveAsync(modFolder, destinationParent, name: null, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await RememberAsync(moved.ToPath, variantInternalName, cancellationToken).ConfigureAwait(false);
            return new ModFilingResult(moved, RememberError: null, previous);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Moved {Mod} but could not remember it was filed by hand", moved.ToPath);
            return new ModFilingResult(moved, ex.Message, previous);
        }
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> UndoMoveAsync(
        string currentFolder,
        string originalFolder,
        string? previousFiling,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFolder);

        var original = PathComparer.Normalize(originalFolder);
        var name = Path.GetFileName(original);

        // Never renamed on the way back: whatever sits in the old place now is the user's.
        if (PathComparer.TryResolveExisting(original, out var taken) && Directory.Exists(taken))
        {
            throw new ModOperationException(
                $"'{name}' could not go back to '{PathDisplay.Show(original)}', because something is there now. " +
                $"It is still at '{PathDisplay.Show(currentFolder)}'.",
                original);
        }

        var parent = Path.GetDirectoryName(original)
                     ?? throw new ModOperationException(
                         $"'{PathDisplay.Show(original)}' has no folder above it to put '{name}' back into.", original);

        var moved = await _files.MoveAsync(currentFolder, parent, name, cancellationToken).ConfigureAwait(false);

        try
        {
            if (previousFiling is { Length: > 0 })
            {
                await RememberAsync(moved.ToPath, previousFiling, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ForgetAsync(moved.ToPath, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Moved {Mod} back but could not restore what it was filed under", moved.ToPath);

            throw new ModOperationException(
                $"'{name}' is back at '{PathDisplay.Show(moved.ToPath)}', but what it was filed under before could not be " +
                $"put back, so a sort may treat it differently: {ex.Message}",
                moved.ToPath);
        }

        _logger.Information("Moved {Mod} back from {From} to {To}", name, currentFolder, moved.ToPath);
        return moved;
    }

    private async Task<string?> PreviousFilingAsync(string modFolder, CancellationToken cancellationToken)
    {
        try
        {
            return (await _configs.ReadAsync(modFolder, cancellationToken).ConfigureAwait(false))?.VariantOverride;
        }
        catch (ModOperationException ex)
        {
            // Unreadable details must not stop the move; the undo then forgets the filing.
            _logger.Warning(ex, "Could not read what {Mod} was filed under before moving it", modFolder);
            return null;
        }
    }
}
