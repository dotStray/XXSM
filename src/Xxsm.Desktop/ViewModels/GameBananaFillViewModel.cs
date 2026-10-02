using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Settings;
using Xxsm.Desktop.Services;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One field the comparison panel offers, with its tick box.</summary>
public sealed partial class GameBananaFillRowViewModel : ObservableObject
{
    /// <summary>Creates the row.</summary>
    /// <param name="entry">What the mod has and what the page says.</param>
    /// <param name="label">The field's name, in the interface's own words.</param>
    /// <param name="empty">What to show where the mod has nothing.</param>
    public GameBananaFillRowViewModel(ModEnrichmentEntry entry, string label, string empty)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        FieldLabel = label;
        CurrentText = entry.Current is { Length: > 0 } current ? OneLine(current) : empty;
        ProposedText = entry.Proposed is { Length: > 0 } proposed ? OneLine(proposed) : string.Empty;
        _isTaken = entry.Selected;
    }

    /// <summary>What the mod has and what the page says.</summary>
    public ModEnrichmentEntry Entry { get; }

    /// <summary>Which field this row is about.</summary>
    public ModEnrichmentField Field => Entry.Field;

    /// <summary>The field's name.</summary>
    public string FieldLabel { get; }

    /// <summary>What the mod has now, on one line.</summary>
    public string CurrentText { get; }

    /// <summary>What the page says, on one line.</summary>
    public string ProposedText { get; }

    /// <summary>Whether this row is ticked; it starts ticked only where the mod has nothing.</summary>
    [ObservableProperty]
    private bool _isTaken;

    private static string OneLine(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Trim();

        return flat.Length <= 90 ? flat : flat[..89] + "…";
    }
}

/// <summary>A mod's GameBanana page beside what the mod has, taking only what is ticked.</summary>
/// <remarks>How <see cref="LinkAsync"/> behaves follows the paste setting; every way links the mod.</remarks>
public sealed partial class GameBananaFillViewModel(
    IGameBananaClient client,
    IModEnrichment enrichment,
    IAppSettingsStore settings,
    INotificationService notifications,
    ViewModelWorkRunner runner,
    ITextCatalogue text,
    IUrlLauncher urls,
    Func<CancellationToken, Task> rescan) : ObservableObject
{
    private readonly IGameBananaClient _client = client;
    private readonly IModEnrichment _enrichment = enrichment;
    private readonly IAppSettingsStore _settings = settings;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _runner = runner;
    private readonly ITextCatalogue _text = text;
    private readonly IUrlLauncher _urls = urls;
    private readonly Func<CancellationToken, Task> _rescan = rescan;

    private ModEnrichmentPlan? _plan;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Whether the page is being read.</summary>
    [ObservableProperty]
    private bool _isReading;

    /// <summary>The mod page's title, for the subtitle.</summary>
    [ObservableProperty]
    private string? _modName;

    /// <summary>The mod's page, for the link.</summary>
    [ObservableProperty]
    private string? _pageUrl;

    /// <summary>What happened, or is happening, or null.</summary>
    [ObservableProperty]
    private string? _note;

    /// <summary>One row per field the page has something for.</summary>
    public ObservableCollection<GameBananaFillRowViewModel> Rows { get; } = [];

    /// <summary>Whether there is anything to tick.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Links a mod to its GameBanana page and does whatever the settings say about filling it in.</summary>
    /// <param name="modFolder">The mod's own folder.</param>
    /// <param name="modId">The mod id its address named.</param>
    /// <param name="cancellationToken">Cancels the look-up.</param>
    /// <returns>A task that completes when the panel is up, or the writing is done.</returns>
    public async Task LinkAsync(string modFolder, long modId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFolder);

        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(true);

        if (!settings.GameBanana.Enabled)
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                _text[nameof(Strings.GameBanana_Fill_Heading)],
                _text[nameof(Strings.ModInstall_Url_Off)]);

            return;
        }

        Reset();
        IsReading = true;
        IsOpen = settings.GameBanana.PasteIntoExisting == GameBananaPasteAction.Ask;
        Note = _text[nameof(Strings.GameBanana_Prompt_Reading)];

        ModEnrichmentPlan? plan = null;

        var ran = await _runner.RunAsync(
            _text[nameof(Strings.GameBanana_Fill_Heading)],
            async ct =>
            {
                var page = await _client.GetModAsync(modId, cancellationToken: ct).ConfigureAwait(true);
                plan = await _enrichment.PlanAsync(modFolder, page, ct).ConfigureAwait(true);
            },
            cancellationToken).ConfigureAwait(true);

        IsReading = false;

        if (!ran || plan is null)
        {
            Close();

            return;
        }

        _plan = plan;
        ModName = plan.Mod.Name;
        PageUrl = plan.Mod.PageUrl.AbsoluteUri;

        switch (settings.GameBanana.PasteIntoExisting)
        {
            case GameBananaPasteAction.SaveOnly:
                await WriteAsync([], cancellationToken).ConfigureAwait(true);

                return;

            case GameBananaPasteAction.FillBlanks:
                await WriteAsync(plan.BlankFields, cancellationToken).ConfigureAwait(true);

                return;

            default:
                foreach (var entry in plan.Differences)
                {
                    Rows.Add(new GameBananaFillRowViewModel(
                        entry, LabelOf(entry.Field), _text[nameof(Strings.GameBanana_Fill_Empty)]));
                }

                Note = HasRows ? null : _text[nameof(Strings.GameBanana_Fill_Nothing)];
                OnPropertyChanged(nameof(HasRows));

                // Nothing to choose between: link it and say so.
                if (!HasRows)
                {
                    await WriteAsync([], cancellationToken).ConfigureAwait(true);
                }

                return;
        }
    }

    /// <summary>Writes the ticked fields into the mod.</summary>
    [RelayCommand]
    public Task ApplyAsync() =>
        WriteAsync(
            [.. Rows.Where(row => row.IsTaken).Select(row => row.Field)],
            CancellationToken.None);

    /// <summary>Closes the panel and writes nothing.</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        Reset();
    }

    /// <summary>Opens the mod's GameBanana page in the browser.</summary>
    [RelayCommand]
    public Task OpenPageAsync() => PageUrl is { Length: > 0 } address
        ? _urls.OpenAsync(address, CancellationToken.None)
        : Task.CompletedTask;

    private async Task WriteAsync(
        IReadOnlyCollection<ModEnrichmentField> take, CancellationToken cancellationToken)
    {
        if (_plan is not { } plan)
        {
            return;
        }

        ModEnrichmentResult? result = null;

        var ran = await _runner.RunAsync(
            _text[nameof(Strings.GameBanana_Fill_Heading)],
            async ct => result = await _enrichment.ApplyAsync(plan, take, ct).ConfigureAwait(true),
            cancellationToken).ConfigureAwait(true);

        IsOpen = false;
        Reset();

        if (!ran || result is null)
        {
            return;
        }

        _notifications.Add(
            NotificationSeverity.Information,
            _text[nameof(Strings.GameBanana_Fill_Heading)],
            result.Changed
                ? _text.Format(nameof(Strings.GameBanana_Fill_Applied), _text.Fields(result.Applied.Count))
                : _text[nameof(Strings.GameBanana_Fill_Linked)]);

        if (result.PictureError is { Length: > 0 } pictureError)
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                _text[nameof(Strings.GameBanana_Fill_Heading)],
                _text.Format(nameof(Strings.GameBanana_Fill_PictureFailed), pictureError));
        }

        await _rescan(cancellationToken).ConfigureAwait(true);
    }

    private string LabelOf(ModEnrichmentField which) => which switch
    {
        ModEnrichmentField.Name => _text[nameof(Strings.GameBanana_Field_Name)],
        ModEnrichmentField.Author => _text[nameof(Strings.GameBanana_Field_Author)],
        ModEnrichmentField.Version => _text[nameof(Strings.GameBanana_Field_Version)],
        ModEnrichmentField.Description => _text[nameof(Strings.GameBanana_Field_Description)],
        _ => _text[nameof(Strings.GameBanana_Field_Picture)],
    };

    private void Reset()
    {
        Rows.Clear();
        _plan = null;
        ModName = null;
        PageUrl = null;
        Note = null;
        OnPropertyChanged(nameof(HasRows));
    }
}
