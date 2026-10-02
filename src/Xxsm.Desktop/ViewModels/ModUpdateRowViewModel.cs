using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Io;
using Xxsm.Packs.GameBanana;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One mod with a newer version on GameBanana on the Mods page, with a way to take it off.</summary>
public sealed partial class ModUpdateRowViewModel(
    ModUpdateStatus status,
    IUrlLauncher urls,
    IModUpdateChecker checker,
    Func<Task> refresh,
    Action<string> goTo,
    Func<string, Task> update) : ObservableObject
{
    private readonly IUrlLauncher _urls = urls;
    private readonly IModUpdateChecker _checker = checker;
    private readonly Func<Task> _refresh = refresh;
    private readonly Action<string> _goTo = goTo;
    private readonly Func<string, Task> _update = update;

    /// <summary>Downloads the newer version and shows what it changes, to confirm.</summary>
    /// <returns>A task that completes when the list of changes, or the reason, is on screen.</returns>
    [RelayCommand]
    public Task UpdateAsync() => _update(Status.ModFolder);

    /// <summary>What the last check concluded.</summary>
    public ModUpdateStatus Status { get; } = status;

    /// <summary>What the mod is called.</summary>
    public string DisplayName => Status.DisplayName;

    /// <summary>The mod's own folder, so the row says which one it means.</summary>
    public string ModFolder => Status.ModFolder;

    /// <summary>The version change, written out, or null when nothing recorded one.</summary>
    public string? VersionChange => Status.VersionChange;

    /// <summary>Whether there is a version change to show.</summary>
    public bool HasVersionChange => VersionChange is { Length: > 0 };

    /// <summary>Goes to the mod: its character's page, with it selected.</summary>
    [RelayCommand]
    public void GoTo() => _goTo(Status.ModFolder);

    /// <summary>Opens the mod's GameBanana page in the browser.</summary>
    [RelayCommand]
    public Task OpenPageAsync() => _urls.OpenAsync(Status.PageUrl.AbsoluteUri, CancellationToken.None);

    /// <summary>Agrees that the installed version is current, and takes the row off the list.</summary>
    [RelayCommand]
    public async Task DismissAsync()
    {
        await _checker.AcceptAsync(Status.ModFolder).ConfigureAwait(true);
        await _refresh().ConfigureAwait(true);
    }
}
