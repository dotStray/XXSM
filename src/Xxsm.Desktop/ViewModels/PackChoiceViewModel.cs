using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Packs.Registry;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One game's packs, as the setup flow and the Game Packs page both show them.</summary>
public sealed partial class PackChoiceViewModel : ObservableObject
{
    /// <summary>The game's icon: the installed pack's, or the name's initial when not installed.</summary>
    public GameIconViewModel? Icon { get; init; }

    /// <summary>Creates the choice from a catalogue entry.</summary>
    /// <param name="entry">The catalogue entry.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="withheldUpdates">How many characters the last update withheld changes from; zero in
    /// setup.</param>
    /// <param name="hasCorrections">Whether the user has corrections for the game; false in setup.</param>
    /// <param name="removeOldVersionsAfterDays">How long an old version stays, for the line under the list.</param>
    /// <param name="keepChanged">Saves a version's <em>Keep</em> box; null in setup.</param>
    public PackChoiceViewModel(
        PackCatalogEntry entry,
        ITextCatalogue text,
        int withheldUpdates = 0,
        bool hasCorrections = false,
        int removeOldVersionsAfterDays = PackPreferences.DefaultRemoveOldVersionsAfterDays,
        Action<PackVersionViewModel, bool>? keepChanged = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(text);

        GameId = entry.GameId;
        DisplayName = entry.DisplayName;
        Registry = entry.Registry;
        IsInstalled = entry.IsInstalled;
        Version = PackVersionText.Display(entry.TargetVersion?.PackVersion ?? entry.ActiveVersion);

        SizeText = entry.TargetVersion is { Version.SizeBytes: > 0 } target
            ? FormatSize(target.Version.SizeBytes)
            : string.Empty;

        PublishedLine = entry.TargetVersion?.Version.Changelog;

        // A version this build cannot read is listed, never hidden.
        RefusalReason = entry.Versions.Count > 0 && entry.LatestInstallable is null
            ? entry.Versions[0].RefusalReason
            : null;

        IsInstalled = entry.IsInstalled;
        InstalledVersion = entry.ActiveVersion;
        UpdateAvailable = entry.UpdateAvailable && entry.IsInstalled;

        CanInstall = entry.TargetVersion is { IsInstallable: true } && !entry.IsInstalled;

        NewerThanHeld = entry.NewerThanHeld;

        StatusText = entry switch
        {
            { IsInstalled: false } => text[nameof(Strings.Packs_Status_NotInstalled)],
            { NewerThanHeld: { } newerThanHeld } => text.Format(
                nameof(Strings.Packs_Status_Held),
                PackVersionText.Display(entry.ActiveVersion),
                PackVersionText.Display(newerThanHeld)),
            _ when entry.UpdateAvailable && entry.TargetVersion is { } newer =>
                text.Format(nameof(Strings.Packs_Status_Update), PackVersionText.Display(newer.PackVersion)),
            _ => text.Format(nameof(Strings.Packs_Status_Installed), PackVersionText.Display(entry.ActiveVersion)),
        };

        SourceText = entry.Registry is { Length: > 0 } registry
            ? text.Format(nameof(Strings.Packs_From), registry)
            : string.Empty;

        WithheldUpdates = withheldUpdates;
        HasCorrections = hasCorrections;

        Versions =
        [
            .. entry.InstalledVersions.Select((version, index) => new PackVersionViewModel(
                this,
                version,
                isInUse: string.Equals(version, entry.ActiveVersion, StringComparison.OrdinalIgnoreCase),
                isNewest: index == 0,
                isKept: entry.Preference.Keeps(version),
                keepChanged)),
        ];

        VersionsHeading = text.Format(nameof(Strings.Packs_Versions), Versions.Count);

        VersionsCleanupText = removeOldVersionsAfterDays > 0
            ? text.Format(nameof(Strings.Packs_Versions_Cleanup), text.Days(removeOldVersionsAfterDays))
            : text[nameof(Strings.Packs_Versions_KeepAll)];

        WithheldUpdatesText = withheldUpdates > 0
            ? text.Format(nameof(Strings.Packs_SkippedUpdates), withheldUpdates)
            : string.Empty;
    }

    /// <summary>Every installed version, newest first, with which one is in use.</summary>
    public IReadOnlyList<PackVersionViewModel> Versions { get; }

    /// <summary>Whether there is a choice of versions to show; one version is the card itself.</summary>
    public bool HasSeveralVersions => Versions.Count > 1;

    /// <summary>Whether the list of versions is open; kept when the page rebuilds its cards.</summary>
    public bool IsVersionsExpanded { get; set; }

    /// <summary>"Installed versions (2)".</summary>
    public string VersionsHeading { get; }

    /// <summary>What happens to old versions, under the list.</summary>
    public string VersionsCleanupText { get; }

    /// <summary>Whether the user has corrections or characters of their own for the game.</summary>
    public bool HasCorrections { get; }

    /// <summary>How many characters the last update withheld changes from.</summary>
    public int WithheldUpdates { get; }

    /// <summary>The label on the button that reopens that review.</summary>
    public string WithheldUpdatesText { get; }

    /// <summary>Whether there is a review to reopen.</summary>
    public bool HasWithheldUpdates => WithheldUpdates > 0;

    /// <summary>Whether this game's last update is recorded and in use, so the card offers What's new.</summary>
    public bool HasWhatsNew { get; init; }

    /// <summary>Whether a pack for this game is already on this machine.</summary>
    public bool IsInstalled { get; }

    /// <summary>The version installed, or null when none is.</summary>
    public string? InstalledVersion { get; }

    /// <summary>Whether a newer usable version is published.</summary>
    public bool UpdateAvailable { get; }

    /// <summary>The newer version the game is held back from, or null when it follows updates.</summary>
    public string? NewerThanHeld { get; }

    /// <summary>Whether the game stays on an older version, so the card offers <em>Follow updates</em>.</summary>
    public bool IsHeld => NewerThanHeld is not null;

    /// <summary>What state this game is in, in words.</summary>
    public string StatusText { get; }

    /// <summary>Which source it came from, in words. Empty when it came from none.</summary>
    public string SourceText { get; }

    /// <summary>Whether there is a source worth naming.</summary>
    public bool HasSource => SourceText.Length > 0;

    /// <summary>Whether there is a version worth showing beside the name.</summary>
    public bool HasVersion => Version is { Length: > 0 };

    /// <summary>The game.</summary>
    public string GameId { get; }

    /// <summary>The name to show.</summary>
    public string DisplayName { get; }

    /// <summary>The version that would be installed, or the one already there.</summary>
    public string? Version { get; }

    /// <summary>Which registry it came from.</summary>
    public string? Registry { get; }

    /// <summary>The download size, already formatted, or empty when unknown.</summary>
    public string SizeText { get; }

    /// <summary>The line the pack list carries for the version on offer, which its publisher wrote.</summary>
    public string? PublishedLine { get; }

    /// <summary>What the installed pack holds, counted by the app, or null when nothing is installed.</summary>
    public string? Contents { get; init; }

    /// <summary>The line under the name: what the pack holds, or the pack list's own line if not installed.</summary>
    public string? Summary => Contents ?? PublishedLine;

    /// <summary>Why nothing here can be installed, when that is the case.</summary>
    public string? RefusalReason { get; }

    /// <summary>Whether pressing Install would do anything.</summary>
    public bool CanInstall { get; }

    /// <summary>Whether there is a size worth showing.</summary>
    public bool HasSize => SizeText.Length > 0;

    /// <summary>Whether this game's pack is being installed now, which puts a progress bar on its card.</summary>
    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>How far the install is, 0 to 100.</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>Whether the bar moves without a percentage: the size is not known yet.</summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    /// <summary>What the install is doing, in words: "Downloading… 1.2 MB of 2.8 MB".</summary>
    [ObservableProperty]
    private string? _progressText;

    /// <summary>Shows where an install is up to.</summary>
    public void ShowProgress(PackInstallProgress progress, ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(text);

        IsInstalling = true;

        switch (progress.Stage)
        {
            case PackInstallStage.Downloading when progress.Fraction is { } fraction:
                IsProgressIndeterminate = false;
                ProgressPercent = fraction * 100;
                ProgressText = text.Format(
                    nameof(Strings.Packs_Progress_Downloading),
                    FormatSize(progress.BytesDone),
                    FormatSize(progress.BytesTotal!.Value));
                break;

            case PackInstallStage.Downloading:
                IsProgressIndeterminate = true;
                ProgressText = text.Format(nameof(Strings.Packs_Progress_DownloadingUnknown), FormatSize(progress.BytesDone));
                break;

            default:
                IsProgressIndeterminate = true;
                ProgressPercent = 100;
                ProgressText = text[progress.Stage == PackInstallStage.Checking
                    ? nameof(Strings.Packs_Progress_Checking)
                    : nameof(Strings.Packs_Progress_Installing)];
                break;
        }
    }

    /// <summary>A progress sink for this card's install, from any thread; only visible changes reach it.</summary>
    /// <param name="ui">The UI thread.</param>
    /// <param name="text">The interface's wording.</param>
    /// <returns>What to hand the install.</returns>
    public IProgress<PackInstallProgress> ProgressOn(Services.IUiDispatcher ui, ITextCatalogue text)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(text);

        var last = (Stage: (PackInstallStage?)null, Percent: -1);

        return new Relay(progress =>
        {
            var percent = progress.Fraction is { } fraction ? (int)(fraction * 100) : -1;

            if (last.Stage == progress.Stage && last.Percent == percent && percent >= 0)
            {
                return;
            }

            last = (progress.Stage, percent);
            ui.Post(() => ShowProgress(progress, text));
        });
    }

    /// <summary>Takes the progress bar off the card.</summary>
    public void ClearProgress()
    {
        IsInstalling = false;
        ProgressText = null;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
    }

    /// <summary>Passes each report straight on; <see cref="Progress{T}"/> would reorder them.</summary>
    private sealed class Relay(Action<PackInstallProgress> report) : IProgress<PackInstallProgress>
    {
        public void Report(PackInstallProgress value) => report(value);
    }

    internal static string FormatSize(long bytes)
    {
        const double Mega = 1024d * 1024d;

        return bytes >= Mega
            ? string.Create(CultureInfo.CurrentCulture, $"{bytes / Mega:0.#} MB")
            : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:0} KB");
    }
}
