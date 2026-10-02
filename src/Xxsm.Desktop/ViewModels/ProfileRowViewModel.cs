using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Profiles;

namespace Xxsm.Desktop.ViewModels;

/// <summary>What a profile row asks its page to do.</summary>
internal interface IProfileActions
{
    Task ApplyAsync(ProfileRowViewModel row);

    Task AskToReplaceAsync(ProfileRowViewModel row);

    Task ReplaceAsync(ProfileRowViewModel row);

    Task RenameAsync(ProfileRowViewModel row, string name);

    Task SetReadOnlyAsync(ProfileRowViewModel row, bool isReadOnly);

    Task DeleteAsync(ProfileRowViewModel row);

    Task ShowModsAsync(ProfileRowViewModel row);
}

/// <summary>One saved profile on the Profiles page.</summary>
public sealed partial class ProfileRowViewModel : ObservableObject
{
    private readonly IProfileActions _actions;
    private readonly ITextCatalogue _text;

    /// <summary>Creates the row.</summary>
    /// <param name="profile">The profile.</param>
    /// <param name="actions">The page, which does what the row's buttons ask.</param>
    /// <param name="text">The interface's wording.</param>
    internal ProfileRowViewModel(ModProfile profile, IProfileActions actions, ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(text);

        Profile = profile;
        _actions = actions;
        _text = text;
        _renameText = profile.Name;
    }

    /// <summary>The profile as it was last read.</summary>
    public ModProfile Profile { get; }

    /// <summary>Its name.</summary>
    public string Name => Profile.Name;

    /// <summary>Whether it is protected from being saved over, renamed or deleted.</summary>
    public bool IsReadOnly => Profile.ReadOnly;

    /// <summary>Whether it can be changed at all.</summary>
    public bool IsEditable => !Profile.ReadOnly;

    /// <summary>How many mods it switches on, and when it was saved.</summary>
    public string Subtitle => _text.Format(
        nameof(Strings.Profiles_Row_Subtitle),
        _text.Mods(Profile.Enabled.Count),
        Resources.TextDates.DateAndTime(Profile.UpdatedAt));

    /// <summary>What the read-only switch in the menu says, which is what pressing it would do.</summary>
    public string ReadOnlyText => _text[Profile.ReadOnly ? nameof(Strings.Profiles_MakeEditable) : nameof(Strings.Profiles_MakeReadOnly)];

    /// <summary>Whether the name is being edited in place.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>The name being typed.</summary>
    [ObservableProperty]
    private string _renameText;

    /// <summary>Whether the row is asking to be sure before replacing what the profile holds.</summary>
    [ObservableProperty]
    private bool _isConfirmingReplace;

    /// <summary>The question that asks it.</summary>
    [ObservableProperty]
    private string? _replaceQuestion;

    /// <summary>Whether the row is showing its ordinary face — not renaming, not asking.</summary>
    public bool IsIdle => !IsRenaming && !IsConfirmingReplace;

    /// <summary>Opens the review of what applying would switch.</summary>
    [RelayCommand]
    private Task ApplyAsync() => _actions.ApplyAsync(this);

    /// <summary>Asks before replacing the profile's mods with what is switched on now.</summary>
    /// <param name="question">What to ask, with the count the page has in hand.</param>
    internal void AskToReplace(string question)
    {
        ReplaceQuestion = question;
        IsConfirmingReplace = true;
    }

    /// <summary>Starts asking whether to replace the profile's mods; the page fills in the question.</summary>
    [RelayCommand]
    private Task StartReplaceAsync() => _actions.AskToReplaceAsync(this);

    /// <summary>Replaces the profile's mods with what is switched on now.</summary>
    [RelayCommand]
    private Task ConfirmReplaceAsync()
    {
        IsConfirmingReplace = false;
        ReplaceQuestion = null;
        return _actions.ReplaceAsync(this);
    }

    /// <summary>Stops asking.</summary>
    [RelayCommand]
    private void CancelReplace()
    {
        IsConfirmingReplace = false;
        ReplaceQuestion = null;
    }

    /// <summary>Starts editing the name in place.</summary>
    [RelayCommand]
    private void StartRename()
    {
        RenameText = Profile.Name;
        IsRenaming = true;
    }

    /// <summary>Saves the typed name.</summary>
    [RelayCommand]
    private Task ConfirmRenameAsync() => _actions.RenameAsync(this, RenameText);

    /// <summary>Stops editing the name without saving it.</summary>
    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameText = Profile.Name;
    }

    /// <summary>Protects the profile, or stops protecting it.</summary>
    [RelayCommand]
    private Task ToggleReadOnlyAsync() => _actions.SetReadOnlyAsync(this, !Profile.ReadOnly);

    /// <summary>Moves the profile to the trash, with an Undo on its notice.</summary>
    [RelayCommand]
    private Task DeleteAsync() => _actions.DeleteAsync(this);

    /// <summary>Opens the profile's mods, to see them and take any off.</summary>
    [RelayCommand]
    private Task ShowModsAsync() => _actions.ShowModsAsync(this);

    partial void OnIsRenamingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnIsConfirmingReplaceChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
}
