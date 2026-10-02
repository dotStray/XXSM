using Serilog;
using Xxsm.Core;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Packs.Pictures;

namespace Xxsm.Packs.GameBanana;

/// <summary>The default <see cref="IModEnrichment"/>.</summary>
public sealed class ModEnrichmentService(
    IModConfigStore configs,
    IModPreviewEditor previews,
    IPictureDownloader pictures,
    IAppSettingsStore settings,
    ILogger logger,
    TimeProvider? time = null) : IModEnrichment
{
    private readonly IModConfigStore _configs = configs;
    private readonly IModPreviewEditor _previews = previews;
    private readonly IPictureDownloader _pictures = pictures;
    private readonly IAppSettingsStore _settings = settings;
    private readonly ILogger _logger = logger.ForContext<ModEnrichmentService>();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<ModEnrichmentPlan> PlanAsync(
        string modFolder, GameBananaMod page, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);
        ArgumentNullException.ThrowIfNull(page);

        if (!PathComparer.TryResolveExisting(modFolder, out var resolved) || !Directory.Exists(resolved))
        {
            throw new ModOperationException($"There is no mod folder at '{PathDisplay.Show(modFolder)}'.", modFolder);
        }

        var config = await _configs.ReadAsync(resolved, cancellationToken).ConfigureAwait(false);
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

        var entries = new List<ModEnrichmentEntry>
        {
            Entry(ModEnrichmentField.Name, config?.CustomName, page.Name),
            Entry(ModEnrichmentField.Author, config?.Author, page.Author),
            Entry(ModEnrichmentField.Version, config?.Version, page.Version),
            Entry(ModEnrichmentField.Description, config?.Description, GameBananaText.ForStorage(page.Description)),
        };

        // Not offered at all when pictures are off: a tick box that does nothing is worse.
        if (settings.GameBanana.DownloadPicturesOrDefault && page.PreviewImageUrl is { } picture)
        {
            entries.Add(Entry(
                ModEnrichmentField.Picture,
                config is { NoImage: false, ImagePath: { Length: > 0 } stored } ? stored : null,
                Path.GetFileName(picture.AbsolutePath)));
        }

        return new ModEnrichmentPlan
        {
            ModFolder = resolved,
            Mod = page,
            Entries = entries,
        };
    }

    /// <inheritdoc />
    public async Task<ModEnrichmentResult> ApplyAsync(
        ModEnrichmentPlan plan,
        IReadOnlyCollection<ModEnrichmentField> take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(take);

        var mod = plan.Mod;
        var wanted = plan.Entries
            .Where(entry => entry.Changes && take.Contains(entry.Field))
            .Select(entry => entry.Field)
            .ToHashSet();

        string? pictureError = null;
        var applied = new List<ModEnrichmentField>();

        // The picture first: the preview editor writes mod.json itself, and later would overwrite it.
        if (wanted.Contains(ModEnrichmentField.Picture) && mod.PreviewImageUrl is { } address)
        {
            try
            {
                var image = await _pictures.DownloadAsync(address, cancellationToken).ConfigureAwait(false);
                await _previews.SetAsync(plan.ModFolder, image, cancellationToken).ConfigureAwait(false);
                applied.Add(ModEnrichmentField.Picture);
            }
            catch (ModOperationException ex)
            {
                pictureError = ex.Message;

                _logger.Warning(
                    ex,
                    "Could not take the preview picture for mod {ModId} from {Address}",
                    mod.ModId,
                    address);
            }
        }

        var config = await _configs.UpdateAsync(
            plan.ModFolder,
            current =>
            {
                var updated = current;

                if (wanted.Contains(ModEnrichmentField.Name))
                {
                    updated = updated with { CustomName = mod.Name };
                    applied.Add(ModEnrichmentField.Name);
                }

                if (wanted.Contains(ModEnrichmentField.Author))
                {
                    updated = updated with { Author = mod.Author };
                    applied.Add(ModEnrichmentField.Author);
                }

                if (wanted.Contains(ModEnrichmentField.Version))
                {
                    updated = updated with { Version = mod.Version };
                    applied.Add(ModEnrichmentField.Version);
                }

                if (wanted.Contains(ModEnrichmentField.Description))
                {
                    updated = updated with { Description = GameBananaText.ForStorage(mod.Description) };
                    applied.Add(ModEnrichmentField.Description);
                }

                // Always: the address and ids are what link this mod to that page.
                return updated with
                {
                    ModUrl = mod.PageUrl.AbsoluteUri,
                    GameBanana = (updated.GameBanana ?? new ModGameBananaInfo()) with
                    {
                        ModId = mod.ModId,
                        DateModifiedTs = mod.DateModifiedTs,
                        LastChecked = _time.GetUtcNow(),
                        UpdateAvailable = false,
                    },
                };
            },
            cancellationToken).ConfigureAwait(false);

        _logger.Information(
            "Filled mod at {Path} in from GameBanana {ModId}: {Fields}",
            plan.ModFolder,
            mod.ModId,
            applied.Count == 0 ? "the address only" : string.Join(", ", applied));

        return new ModEnrichmentResult(config, applied, pictureError);
    }

    private static ModEnrichmentEntry Entry(ModEnrichmentField which, string? current, string? proposed)
    {
        var have = current?.Trim() is { Length: > 0 } value ? value : null;
        var offered = proposed?.Trim() is { Length: > 0 } candidate ? candidate : null;

        return new ModEnrichmentEntry(which, have, offered, have is null && offered is not null);
    }
}
