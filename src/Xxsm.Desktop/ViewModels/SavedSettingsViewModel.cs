using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core;
using Xxsm.Core.Ini;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One default about to change: a setting, what it starts at now, and what it will start at.</summary>
/// <param name="Setting">The setting as it was read.</param>
/// <param name="To">The new default.</param>
public sealed record SavedSettingChange(SavedSetting Setting, string To)
{
    /// <summary>The setting's name as the mod writes it.</summary>
    public string Name => Setting.Name;

    /// <summary>The default now.</summary>
    public string From => Setting.EffectiveDefault;
}

/// <summary>
/// The settings a mod keeps between game sessions, in its detail pane: a button lists the defaults that would change,
/// and the pane's own Save writes them.
/// </summary>
public sealed partial class SavedSettingsViewModel(ISavedSettingsService settings, ITextCatalogue text) : ObservableObject
{
    private readonly ISavedSettingsService _settings = settings;
    private readonly ITextCatalogue _text = text;
    private IReadOnlyList<SavedSetting> _read = [];
    private int _version;

    /// <summary>The mod whose settings are showing, or <c>null</c>.</summary>
    public string? ModFolder { get; private set; }

    /// <summary>The defaults Save would write, filled by one of the two buttons.</summary>
    public ObservableCollection<SavedSettingChange> Changes { get; } = [];

    /// <summary>What could not be read, each as a sentence.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>A sentence under the buttons after one found nothing to change, or a refusal; <c>null</c> otherwise.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>Whether the mod keeps any settings, so the section is shown.</summary>
    public bool HasSettings => _read.Count > 0;

    /// <summary>Whether XXSM has changed a default, so the mod's own can be put back.</summary>
    public bool CanRestore => _read.Any(setting => setting.DiffersFromOriginal);

    /// <summary>Whether anything could not be read.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>Whether Save has defaults to write.</summary>
    public bool IsDirty => Changes.Count > 0;

    /// <summary>Over the list: how many settings change, and what Save and Revert do.</summary>
    public string ChangesText => _text.Format(nameof(Strings.SavedSettings_Changes), _text.Settings(Changes.Count));

    /// <summary>Reads a mod's settings, or clears the section; anything waiting to be saved is dropped.</summary>
    /// <param name="modFolder">The mod, or <c>null</c> for none.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task LoadAsync(string? modFolder, CancellationToken cancellationToken)
    {
        // Selecting quickly starts several reads; only the last one's answer is shown.
        var version = ++_version;
        var read = await ReadAsync(modFolder, cancellationToken).ConfigureAwait(true);

        if (version != _version)
        {
            return;
        }

        ModFolder = modFolder;
        Show(read);
        Revert();
    }

    /// <summary>Reads the game's saved values again and lists each default that differs from them.</summary>
    [RelayCommand]
    private Task UseInGameAsync(CancellationToken cancellationToken) =>
        PrepareAsync(
            read => read.Settings.Where(setting => setting.DiffersFromGame).Select(setting => new SavedSettingChange(setting, setting.InGame!)),
            read => read.Settings.Any(setting => setting.InGame is not null)
                ? nameof(Strings.SavedSettings_AlreadyMatch)
                : nameof(Strings.SavedSettings_NothingSaved),
            cancellationToken);

    /// <summary>Lists each default that differs from the one the mod came with.</summary>
    [RelayCommand]
    private Task RestoreAsync(CancellationToken cancellationToken) =>
        PrepareAsync(
            read => read.Settings.Where(setting => setting.DiffersFromOriginal).Select(setting => new SavedSettingChange(setting, setting.Original!)),
            _ => nameof(Strings.SavedSettings_AlreadyOriginal),
            cancellationToken);

    /// <summary>Writes the listed defaults into the mod's INIs and reads them again.</summary>
    /// <exception cref="ModOperationException">A default changed since it was read, or a file could not be written.</exception>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!IsDirty || ModFolder is not { } folder)
        {
            return;
        }

        var edits = Changes.Select(change => new SavedSettingEdit(change.Setting.File, change.Setting.Line, change.Setting.Default, change.To)).ToList();

        await _settings.WriteAsync(folder, edits, cancellationToken).ConfigureAwait(true);
        await LoadAsync(folder, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Drops the listed defaults; the mod's file is left as it is.</summary>
    public void Revert()
    {
        Changes.Clear();
        Status = null;
        Changed();
    }

    private async Task PrepareAsync(
        Func<SavedSettingsReadResult, IEnumerable<SavedSettingChange>> changes,
        Func<SavedSettingsReadResult, string> nothingKey,
        CancellationToken cancellationToken)
    {
        if (ModFolder is not { } folder)
        {
            return;
        }

        var version = _version;
        var read = await ReadAsync(folder, cancellationToken).ConfigureAwait(true);

        if (version != _version)
        {
            return;
        }

        Show(read);
        Changes.Clear();

        foreach (var change in changes(read))
        {
            Changes.Add(change);
        }

        Status = Changes.Count == 0 && read.Settings.Count > 0 ? _text[nothingKey(read)] : null;
        Changed();
    }

    private async Task<SavedSettingsReadResult> ReadAsync(string? modFolder, CancellationToken cancellationToken)
    {
        if (modFolder is not { Length: > 0 })
        {
            return new SavedSettingsReadResult([], null, []);
        }

        try
        {
            return await _settings.ReadAsync(modFolder, cancellationToken).ConfigureAwait(true);
        }
        catch (ModOperationException ex)
        {
            return new SavedSettingsReadResult([], null, [ex.Message]);
        }
    }

    private void Show(SavedSettingsReadResult read)
    {
        _read = read.Settings;
        Problems.Clear();

        foreach (var problem in read.Problems)
        {
            Problems.Add(problem);
        }

        OnPropertyChanged(nameof(HasSettings));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(HasProblems));
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ChangesText));
    }
}
