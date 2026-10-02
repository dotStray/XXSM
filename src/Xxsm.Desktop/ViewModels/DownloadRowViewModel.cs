using CommunityToolkit.Mvvm.ComponentModel;
using Xxsm.Core.Text;
using Xxsm.Packs.Downloads;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One line of the download list, read live from the job so every view shows the same number.</summary>
public sealed partial class DownloadRowViewModel(DownloadJob job, ITextCatalogue text) : ObservableObject
{
    private readonly ITextCatalogue _text = text;

    /// <summary>The download itself.</summary>
    public DownloadJob Job { get; } = job;

    /// <summary>Its id.</summary>
    public string Id => Job.Id;

    /// <summary>What to call it.</summary>
    public string DisplayName =>
        Job.Record.Name ?? Job.Record.FileName ?? _text[nameof(Strings.Downloads_Unnamed)];

    /// <summary>The author and version, when the page carried them.</summary>
    public string? Subtitle => (Job.Record.Author, Job.Record.Version) switch
    {
        ({ Length: > 0 } author, { Length: > 0 } version) => $"{author} · {version}",
        ({ Length: > 0 } author, _) => author,
        (_, { Length: > 0 } version) => version,
        _ => null,
    };

    /// <summary>Whether there is an author or a version to show.</summary>
    public bool HasSubtitle => Subtitle is { Length: > 0 };

    /// <summary>Whether it is still going.</summary>
    public bool IsRunning => Job.IsRunning;

    /// <summary>What state it is in, in a word.</summary>
    public string StateText => Job.State switch
    {
        DownloadState.Running => _text[nameof(Strings.Downloads_State_Running)],
        DownloadState.Ready => _text[nameof(Strings.Downloads_State_Ready)],
        DownloadState.Installed => _text[nameof(Strings.Downloads_State_Installed)],
        DownloadState.Cancelled => _text[nameof(Strings.Downloads_State_Cancelled)],
        DownloadState.Failed => _text[nameof(Strings.Downloads_State_Failed)],
        DownloadState.Blocked => _text[nameof(Strings.Downloads_State_Blocked)],
        _ => string.Empty,
    };

    /// <summary>How much has arrived, against how much there is.</summary>
    public string ProgressText => Job.TotalBytes is > 0
        ? _text.Format(
            nameof(Strings.Downloads_Progress),
            ByteSize.Describe(Job.Bytes),
            ByteSize.Describe(Job.TotalBytes.Value))
        : ByteSize.Describe(Job.Bytes);

    /// <summary>How fast it is going, or null before that can be said.</summary>
    public string? RateText => Job.BytesPerSecond is { } rate
        ? _text.Format(nameof(Strings.Downloads_Rate), ByteSize.Describe((long)rate))
        : null;

    /// <summary>Whether there is a speed to show.</summary>
    public bool HasRate => RateText is { Length: > 0 };

    /// <summary>How much longer, or null when that cannot be estimated.</summary>
    public string? RemainingText => Job.Remaining is { } left
        ? _text.Format(nameof(Strings.Downloads_Remaining), ByteSize.DescribeDuration(left))
        : null;

    /// <summary>Whether there is an estimate to show.</summary>
    public bool HasRemaining => RemainingText is { Length: > 0 };

    /// <summary>How far along, out of 100, for a bar.</summary>
    public double Percent => Job.Fraction is { } fraction ? fraction * 100 : 0;

    /// <summary>Whether the bar has to sweep rather than fill, because nobody said how big the file is.</summary>
    public bool IsIndeterminate => IsRunning && Job.Fraction is null;

    /// <summary>Why it stopped, when something went wrong.</summary>
    public string? Error => Job.Record.Error;

    /// <summary>Whether there is a reason to show.</summary>
    public bool HasError => Job.State is DownloadState.Failed or DownloadState.Blocked
        && Error is { Length: > 0 };

    /// <summary>Whether this entry was started for the game that is open now. Set by the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(IsForAnotherGame))]
    private bool _isForThisGame = true;

    /// <summary>Whether it belongs to a game other than the one that is open.</summary>
    public bool IsForAnotherGame => !IsForThisGame;

    /// <summary>Whether the archive is still there to install from, into this game.</summary>
    public bool CanInstall => IsForThisGame && Job.Record.HasArchive;

    /// <summary>Whether it can be fetched again.</summary>
    public bool CanRetry => Job.State is DownloadState.Cancelled or DownloadState.Failed
        or DownloadState.Blocked or DownloadState.Installed;

    /// <summary>Whether the mod has a page to open.</summary>
    public bool HasPage => Job.Record.PageUrl is { Length: > 0 };

    /// <summary>The mod's page.</summary>
    public string? PageUrl => Job.Record.PageUrl;

    /// <summary>The game whose Mods folder it was started from, when one was named.</summary>
    public string? GameId => Job.Record.GameId;

    /// <summary>Tells the interface that the numbers have moved.</summary>
    public void RaiseProgress()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(HasRate));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(HasRemaining));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    /// <summary>Tells the interface that everything about it may have changed.</summary>
    public void RaiseAll()
    {
        RaiseProgress();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasSubtitle));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(HasPage));
        OnPropertyChanged(nameof(PageUrl));
    }
}
