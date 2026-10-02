using Serilog;
using Xxsm.Core.Io;

namespace Xxsm.Core.Mods;

/// <summary>The default <see cref="IModsFolderProbe"/>.</summary>
public sealed class ModsFolderProbe(IModRepository repository, ILogger logger) : IModsFolderProbe
{
    private readonly IModRepository _repository = repository;
    private readonly ILogger _logger = logger.ForContext<ModsFolderProbe>();

    /// <inheritdoc />
    public async Task<ModsFolderProbeResult> ProbeAsync(
        string modsDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var path = PathComparer.Normalize(modsDirectory);

        if (File.Exists(path))
        {
            return new ModsFolderProbeResult
            {
                Path = path,
                Verdict = ModsFolderVerdict.NotADirectory,
                Summary = "That is a file, not a folder. Pick the folder 3DMigoto loads mods from.",
            };
        }

        if (!Directory.Exists(path))
        {
            return new ModsFolderProbeResult
            {
                Path = path,
                Verdict = ModsFolderVerdict.DoesNotExist,
                Summary = "There is nothing at that path yet.",
            };
        }

        if (TryFindWriteFailure(path) is { } writeFailure)
        {
            return new ModsFolderProbeResult
            {
                Path = path,
                Verdict = ModsFolderVerdict.NotWritable,
                Summary =
                    $"XXSM cannot write into that folder: {writeFailure} " +
                    "Every change XXSM makes is a move inside it, so nothing would work.",
            };
        }

        ModsInventory inventory;

        try
        {
            inventory = await _repository.ScanAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (ModOperationException ex)
        {
            _logger.Warning(ex, "Could not scan the candidate Mods folder {Path}", path);

            return new ModsFolderProbeResult
            {
                Path = path,
                Verdict = ModsFolderVerdict.NotWritable,
                Summary = ex.Message,
            };
        }

        var verdict = inventory.ModCount == 0 && inventory.VariantFolders.Count == 0
            ? ModsFolderVerdict.Empty
            : ModsFolderVerdict.Usable;

        return new ModsFolderProbeResult
        {
            Path = path,
            Verdict = verdict,
            Summary = Describe(inventory, verdict),
            Inventory = inventory,
        };
    }

    private static string Describe(ModsInventory inventory, ModsFolderVerdict verdict)
    {
        if (verdict == ModsFolderVerdict.Empty)
        {
            return "The folder is empty. That is fine — XXSM will fill it.";
        }

        var parts = new List<string>(3);

        if (inventory.ModCount > 0)
        {
            parts.Add(Count(inventory.ModCount, "mod", "mods"));
        }

        if (inventory.VariantFolders.Count > 0)
        {
            parts.Add(Count(inventory.VariantFolders.Count, "character folder", "character folders"));
        }

        if (inventory.UnfiledMods.Count > 0)
        {
            parts.Add($"{inventory.UnfiledMods.Count} not filed under a character yet");
        }

        return parts.Count == 0
            ? "The folder holds nothing XXSM recognises as a mod."
            : $"Found {string.Join(", ", parts)}.";
    }

    private static string Count(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    /// <summary>The system's words for why the folder cannot be written, or null; tests with a probe file.</summary>
    private string? TryFindWriteFailure(string directory)
    {
        var probe = Path.Combine(directory, $".xxsm-write-probe-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryRemoveProbe(probe);
            return ex.Message;
        }
    }

    private void TryRemoveProbe(string probe)
    {
        try
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Could not remove the write probe {Path}", probe);
        }
    }
}
