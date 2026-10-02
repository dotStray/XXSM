using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Xxsm.Core.Profiles;

namespace Xxsm.Desktop.ViewModels;

/// <summary>"Apply Evening?": what a profile would switch on and off, whole, before anything is switched.</summary>
public sealed partial class ProfileApplyViewModel(
    ITextCatalogue text,
    Func<ProfileApplyPlan, Task> apply) : ObservableObject
{
    private readonly ITextCatalogue _text = text;
    private readonly Func<ProfileApplyPlan, Task> _apply = apply;
    private ProfileApplyPlan? _plan;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>"Apply Evening?", or "Switch every mod off?"</summary>
    [ObservableProperty]
    private string _heading = string.Empty;

    /// <summary>What applying does, under the heading.</summary>
    [ObservableProperty]
    private string _body = string.Empty;

    /// <summary>The mods it would switch on, by name.</summary>
    public ObservableCollection<string> SwitchOn { get; } = [];

    /// <summary>The mods it would switch off, by name.</summary>
    public ObservableCollection<string> SwitchOff { get; } = [];

    /// <summary>The mods it names that are not in the Mods folder any more.</summary>
    public ObservableCollection<string> Missing { get; } = [];

    /// <summary>One sentence per mod that is there more than once, saying which copy will be on.</summary>
    public ObservableCollection<string> Copies { get; } = [];

    /// <summary>"Switch on: 3 mods".</summary>
    [ObservableProperty]
    private string _switchOnLabel = string.Empty;

    /// <summary>"Switch off: 17 mods".</summary>
    [ObservableProperty]
    private string _switchOffLabel = string.Empty;

    /// <summary>"Not in the Mods folder any more: 2 mods".</summary>
    [ObservableProperty]
    private string _missingLabel = string.Empty;

    /// <summary>What the button that applies it says.</summary>
    [ObservableProperty]
    private string _applyText = string.Empty;

    /// <summary>Whether applying would change anything.</summary>
    [ObservableProperty]
    private bool _canApply;

    /// <summary>Said instead of the lists when nothing would change.</summary>
    [ObservableProperty]
    private string? _nothingText;

    /// <summary>Whether there is anything to switch on.</summary>
    public bool HasSwitchOn => SwitchOn.Count > 0;

    /// <summary>Whether there is anything to switch off.</summary>
    public bool HasSwitchOff => SwitchOff.Count > 0;

    /// <summary>Whether any mod it names has gone.</summary>
    public bool HasMissing => Missing.Count > 0;

    /// <summary>Whether any mod it names is there twice.</summary>
    public bool HasCopies => Copies.Count > 0;

    /// <summary>Shows what applying a profile would do.</summary>
    public void Open(ProfileApplyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Show(
            plan,
            _text.Format(nameof(Strings.ProfileApply_Heading), plan.Profile.Name),
            _text[nameof(Strings.ProfileApply_Body)],
            _text.Format(nameof(Strings.ProfileApply_Nothing), plan.Profile.Name));
    }

    /// <summary>Shows what switching every mod off would do: the same lists, its own words.</summary>
    /// <param name="plan">A plan from <see cref="IProfileService.PlanAllOffAsync"/>.</param>
    public void OpenAllOff(ProfileApplyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Show(
            plan,
            _text[nameof(Strings.AllOff_Heading)],
            _text[nameof(Strings.AllOff_Body)],
            _text[nameof(Strings.AllOff_Nothing)]);
    }

    private void Show(ProfileApplyPlan plan, string heading, string body, string nothing)
    {
        _plan = plan;
        Heading = heading;
        Body = body;

        Fill(SwitchOn, plan.Switches.Where(change => change.Enable).Select(change => change.Mod.DisplayName));
        Fill(SwitchOff, plan.Switches.Where(change => !change.Enable).Select(change => change.Mod.DisplayName));
        Fill(Missing, plan.Missing.Select(entry => entry.Name is { Length: > 0 } name ? name : entry.Path));
        Fill(Copies, plan.Copies.Select(copy => _text.Format(
            nameof(Strings.ProfileApply_Copy), copy.Chosen.DisplayName, copy.Chosen.VariantFolderName ?? copy.Chosen.Name)));

        SwitchOnLabel = _text.Format(nameof(Strings.ProfileApply_On), _text.Mods(SwitchOn.Count));
        SwitchOffLabel = _text.Format(nameof(Strings.ProfileApply_Off), _text.Mods(SwitchOff.Count));
        MissingLabel = _text.Format(nameof(Strings.ProfileApply_Missing), _text.Mods(Missing.Count));
        ApplyText = _text.Format(nameof(Strings.ProfileApply_Apply), _text.Mods(plan.Switches.Count));
        CanApply = !plan.IsEmpty;
        NothingText = plan.IsEmpty ? nothing : null;

        OnPropertyChanged(nameof(HasSwitchOn));
        OnPropertyChanged(nameof(HasSwitchOff));
        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasCopies));

        IsOpen = true;
    }

    /// <summary>Closes the panel without switching anything.</summary>
    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _plan = null;
    }

    /// <summary>Switches what the panel lists.</summary>
    [RelayCommand]
    private Task ApplyAsync()
    {
        if (_plan is not { } plan || !CanApply)
        {
            return Task.CompletedTask;
        }

        Close();
        return _apply(plan);
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
