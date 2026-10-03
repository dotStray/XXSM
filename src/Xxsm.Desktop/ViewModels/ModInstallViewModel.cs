using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Xxsm.Core;
using Xxsm.Core.Archives;
using Xxsm.Core.GameBanana;
using Xxsm.Core.Io;
using Xxsm.Core.Mods;
using Xxsm.Core.Settings;
using Xxsm.Core.Text;
using Xxsm.Desktop.Services;
using Xxsm.Packs.Downloads;
using Xxsm.Packs.GameBanana;
using Xxsm.Packs.Installation;
using Xxsm.Packs.Pictures;
using Xxsm.Packs.Sorting;

namespace Xxsm.Desktop.ViewModels;

/// <summary>One mod an install source contains, as the confirm step shows it.</summary>
public sealed partial class InstallRowViewModel : ObservableObject, IDisposable
{
    /// <summary>Decode width for a pasted picture: twice the card's picture column, for HiDPI.</summary>
    private const int DecodeWidthPixels = 520;

    private readonly ITextCatalogue _text;
    private readonly IClipboardImageReader _clipboard;
    private readonly IStoragePicker _picker;
    private readonly IPictureDownloader _downloader;
    private readonly IGameBananaClient _gameBanana;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger _logger;
    private PreviewImageSource? _chosenPicture;
    private bool _disposed;

    /// <summary>Creates the row.</summary>
    /// <param name="candidate">The candidate it stands for.</param>
    /// <param name="targets">Every character the mod could be filed under.</param>
    /// <param name="text">The interface's wording.</param>
    /// <param name="clipboard">Reads a pasted picture.</param>
    /// <param name="picker">Chooses a picture file.</param>
    /// <param name="downloader">Fetches a picture dragged in from a web page.</param>
    /// <param name="gameBanana">Reads a mod's page when the address box is filled in and Fetch is pressed.</param>
    /// <param name="settings">Read for whether looking mods up is allowed at all.</param>
    /// <param name="logger">Where a picture that will not decode is recorded.</param>
    /// <param name="filesExpanded">Whether the file list starts open: when the source held one mod.</param>
    public InstallRowViewModel(
        InstallCandidate candidate,
        IReadOnlyList<CharacterChoiceViewModel> targets,
        ITextCatalogue text,
        IClipboardImageReader clipboard,
        IStoragePicker picker,
        IPictureDownloader downloader,
        IGameBananaClient gameBanana,
        IAppSettingsStore settings,
        ILogger logger,
        bool filesExpanded = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(gameBanana);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        Candidate = candidate;
        Targets = targets;
        _text = text;
        _clipboard = clipboard;
        _picker = picker;
        _downloader = downloader;
        _gameBanana = gameBanana;
        _settings = settings;
        _logger = logger.ForContext<InstallRowViewModel>();
        _name = candidate.Name;
        _displayName = candidate.Name;
        _areFilesExpanded = filesExpanded;

        _target = candidate.SuggestedVariantId is { Length: > 0 } suggested
            ? targets.FirstOrDefault(choice =>
                string.Equals(choice.InternalName, suggested, StringComparison.OrdinalIgnoreCase))
            : null;

        if (candidate.PreviewPath is { Length: > 0 } own)
        {
            PictureReady = ShowAsync(PreviewImageSource.FromFile(own));
        }
    }

    /// <summary>The candidate. Installing hands these back, not the view models.</summary>
    public InstallCandidate Candidate { get; }

    /// <summary>Every character the mod could be filed under.</summary>
    public IReadOnlyList<CharacterChoiceViewModel> Targets { get; }

    /// <summary>Whether this mod will be installed. Every row starts ticked.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>The folder name to install it as. Editable.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>What to call it; starts as the folder name and is written only when changed.</summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>Who made it. Written to <c>.xxsm/mod.json</c> when filled in.</summary>
    [ObservableProperty]
    private string _author = string.Empty;

    /// <summary>The mod's page address, written to <c>.xxsm/mod.json</c>; a GameBanana one can be fetched.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetch))]
    private string _modUrl = string.Empty;

    /// <summary>The author's own version string, from the mod's page. Written to <c>mod.json</c>.</summary>
    [ObservableProperty]
    private string _version = string.Empty;

    /// <summary>What the mod is, as plain text from its page. Written to <c>mod.json</c>.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Where on GameBanana this came from, which update checks compare against; not shown.</summary>
    public ModGameBananaInfo? GameBanana { get; set; }

    /// <summary>Whether the page is being read right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFetch))]
    private bool _isFetching;

    /// <summary>What the last Fetch did or could not do, or null.</summary>
    [ObservableProperty]
    private string? _fetchNote;

    /// <summary>Whether there is an address worth reading and nothing already reading it.</summary>
    public bool CanFetch => !IsFetching && GameBananaUrl.TryParseModId(ModUrl.Trim(), out _);

    /// <summary>Reads the mod's GameBanana page and fills in only what this card still has blank.</summary>
    /// <returns>A task that completes when the card has been filled in, or the reason has been shown.</returns>
    [RelayCommand]
    public async Task FetchAsync()
    {
        if (!GameBananaUrl.TryParseModId(ModUrl.Trim(), out var modId))
        {
            FetchNote = _text[nameof(Strings.ModInstall_Url_NotGameBanana)];

            return;
        }

        IsFetching = true;
        FetchNote = _text[nameof(Strings.ModInstall_Url_Fetching)];

        try
        {
            var settings = await _settings.ReadAsync(CancellationToken.None).ConfigureAwait(true);

            if (!settings.GameBanana.Enabled)
            {
                FetchNote = _text[nameof(Strings.ModInstall_Url_Off)];

                return;
            }

            var page = await _gameBanana.GetModAsync(modId, cancellationToken: CancellationToken.None)
                .ConfigureAwait(true);

            Fill(page);

            // Only when the card has no picture of its own.
            if (settings.GameBanana.DownloadPicturesOrDefault
                && page.PreviewImageUrl is { } picture
                && !HasPreview)
            {
                await UsePictureAsync(
                    await _downloader.DownloadAsync(picture, CancellationToken.None).ConfigureAwait(true))
                    .ConfigureAwait(true);
            }

            FetchNote = _text[nameof(Strings.ModInstall_Url_Fetched)];
        }
        catch (XxsmException ex)
        {
            FetchNote = ex.Message;
        }
        finally
        {
            IsFetching = false;
        }
    }

    /// <summary>Puts a page's words into whichever boxes are still empty; the folder name is left alone.</summary>
    /// <param name="page">What GameBanana said.</param>
    internal void Fill(GameBananaMod page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (DisplayName.Trim().Length == 0 || string.Equals(DisplayName, Candidate.Name, StringComparison.Ordinal))
        {
            DisplayName = page.Name ?? DisplayName;
        }

        if (Author.Trim().Length == 0 && page.Author is { Length: > 0 } author)
        {
            Author = author;
        }

        if (Version.Trim().Length == 0 && page.Version is { Length: > 0 } version)
        {
            Version = version;
        }

        if (Description.Trim().Length == 0
            && GameBananaText.ForStorage(page.Description) is { Length: > 0 } description)
        {
            Description = description;
        }

        ModUrl = page.PageUrl.AbsoluteUri;
    }

    /// <summary>Anything the user wants to keep with it. Written to <c>.xxsm/mod.json</c>.</summary>
    [ObservableProperty]
    private string _notes = string.Empty;

    /// <summary>Whether the list of files this mod holds is showing.</summary>
    [ObservableProperty]
    private bool _areFilesExpanded;

    /// <summary>The character it will be filed under, overriding the sorter when changed.</summary>
    [ObservableProperty]
    private CharacterChoiceViewModel? _target;

    /// <summary>Where it sits inside the archive, or empty for the source itself.</summary>
    public string RelativePath => Candidate.RelativePath;

    /// <summary>Whether it came from somewhere inside the source worth naming.</summary>
    public bool HasRelativePath => Candidate.RelativePath.Length > 0;

    /// <summary>The sorter's own account of the decision.</summary>
    public string Reason => Candidate.Reason;

    /// <summary>The folder it will land in, following any override.</summary>
    public string FolderText => Target is null
        ? Candidate.SuggestedFolderName
        : Target.DisplayName;

    /// <summary>How many hashes, files and bytes it carries.</summary>
    public string DetailText => _text.Format(
        nameof(Strings.ModInstall_Detail),
        _text.Hashes(Candidate.Learned.Hashes.Count),
        _text.Files(Candidate.FileCount),
        ByteSize.Describe(Candidate.Bytes));

    /// <summary>Whether the outfit within the family is a guess rather than a hash match.</summary>
    public bool IsUncertain => Candidate.VariantIsUncertain;

    /// <summary>What kind of guess it is, for the badge.</summary>
    public string CertaintyText => Candidate.Learned.Decision.MemberDecidedByName
        ? _text[nameof(Strings.SortReview_Guess_Name)]
        : _text[nameof(Strings.SortReview_Guess_Default)];

    /// <summary>The picture on the card: one the user dropped, pasted or chose, else the mod's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private Bitmap? _previewImage;

    /// <summary>Why the last picture offered could not be used, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPictureError))]
    private string? _pictureError;

    /// <summary>Whether there is a picture to show.</summary>
    public bool HasPreview => PreviewImage is not null;

    /// <summary>Completes once the most recent picture has decoded, or been refused.</summary>
    public Task PictureReady { get; private set; } = Task.CompletedTask;

    /// <summary>Whether <see cref="PictureError"/> has something to say.</summary>
    public bool HasPictureError => PictureError is { Length: > 0 };

    /// <summary>Chooses a picture file for this mod.</summary>
    [RelayCommand]
    private async Task ChoosePictureAsync()
    {
        var picked = await _picker
            .PickFileAsync(
                _text[nameof(Strings.ModImage_PickerTitle)],
                [new FileTypeFilter(_text[nameof(Strings.ModImage_FileType)], [.. IModPreviewEditor.SupportedExtensions])])
            .ConfigureAwait(true);

        if (picked is { Length: > 0 })
        {
            await UsePictureAsync(PreviewImageSource.FromFile(picked)).ConfigureAwait(true);
        }
    }

    /// <summary>Uses the picture on the clipboard: Ctrl+V on the picture, or the right-click menu.</summary>
    [RelayCommand]
    private async Task PastePictureAsync()
    {
        if (await _clipboard.ReadAsync().ConfigureAwait(true) is not { } image)
        {
            PictureError = _text[nameof(Strings.ModImage_NothingToPaste)];
            return;
        }

        await UsePictureAsync(image).ConfigureAwait(true);
    }

    /// <summary>Uses an image file dropped onto the card's picture.</summary>
    /// <returns>A task that completes when the picture is showing, or has been refused.</returns>
    public Task DropPictureAsync(string path) => UsePictureAsync(PreviewImageSource.FromFile(path));

    /// <summary>Shows a picture the panel already fetched from the mod's GameBanana page.</summary>
    /// <returns>A task that completes when it is showing, or has been refused.</returns>
    internal Task UseFetchedPictureAsync(PreviewImageSource picture) => UsePictureAsync(picture);

    /// <summary>Uses a picture dropped onto the card: a file, or one from a web page, fetched first.</summary>
    /// <returns>A task that completes when the picture is showing, or has been refused.</returns>
    public async Task DropPictureAsync(PictureDrop drop)
    {
        ArgumentNullException.ThrowIfNull(drop);

        PreviewImageSource picture;

        try
        {
            picture = await drop.ToSourceAsync(_downloader, _text, CancellationToken.None).ConfigureAwait(true);
        }
        catch (ModOperationException exception)
        {
            PictureError = exception.Message;
            return;
        }

        await UsePictureAsync(picture).ConfigureAwait(true);
    }

    /// <summary>Shows a picture and keeps it for the install once decoded; a non-picture is refused here.</summary>
    private async Task UsePictureAsync(PreviewImageSource image)
    {
        if (image.FilePath is { } file && !IModPreviewEditor.IsSupportedImage(file))
        {
            PictureError = _text.Format(nameof(Strings.ModImage_Unsupported), PathDisplay.Show(file));
            return;
        }

        var showing = ShowAsync(image);
        PictureReady = showing;

        if (await showing.ConfigureAwait(true))
        {
            _chosenPicture = image;
        }
    }

    private async Task<bool> ShowAsync(PreviewImageSource image)
    {
        Bitmap decoded;
        byte[] bytes;

        try
        {
            bytes = await image.ReadAsync().ConfigureAwait(true);
        }
        catch (ModOperationException exception)
        {
            PictureError = exception.Message;
            return false;
        }

        // The size is read from the header before anything is decoded.
        if (PictureSize.TryRead(bytes, out var size) && size.IsTooLarge)
        {
            _logger.Warning(
                "Not showing the picture {Source} for {Mod}: {Width} x {Height} pixels",
                image.FilePath ?? "(clipboard)", Candidate.Name, size.Width, size.Height);
            PictureError = _text.Format(nameof(Strings.ModImage_TooLarge), size.Width, size.Height);
            return false;
        }

        try
        {
            // Always scaled down as it is decoded, never at full size.
            decoded = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    return Bitmap.DecodeToWidth(stream, DecodeWidthPixels, BitmapInterpolationMode.HighQuality);
                })
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warning(
                exception, "Could not decode the picture {Source} for {Mod}", image.FilePath ?? "(clipboard)", Candidate.Name);
            PictureError = _text.Format(nameof(Strings.ModImage_Unreadable), exception.Message);
            return false;
        }

        if (_disposed)
        {
            decoded.Dispose();
            return false;
        }

        var previous = PreviewImage;
        PreviewImage = decoded;
        PictureError = null;
        previous?.Dispose();

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var image = PreviewImage;
        PreviewImage = null;
        image?.Dispose();
    }

    /// <summary>The files the mod holds, relative to its own root.</summary>
    public IReadOnlyList<string> Files => Candidate.Files;

    /// <summary>Whether there is a file list to show at all.</summary>
    public bool HasFiles => Candidate.Files.Count > 0;

    /// <summary>The file list's label: how many there are.</summary>
    public string FilesHeaderText => _text.Files(Candidate.FileCount);

    /// <summary>Whether the list stops short of every file the mod holds.</summary>
    public bool IsFileListTruncated => Candidate.FileListIsTruncated;

    /// <summary>The line that says so, when it does.</summary>
    public string FileListTruncatedText => _text.Format(
        nameof(Strings.ModInstall_Files_Truncated),
        _text.Files(Candidate.FileCount - Candidate.Files.Count));

    /// <summary>Where it will land, as a path under the Mods folder.</summary>
    public string DestinationText => _text.Format(
        nameof(Strings.ModInstall_Destination),
        Target?.ModFilesName ?? Candidate.SuggestedFolderName,
        Name.Trim().Length > 0 ? Name.Trim() : Candidate.Name);

    /// <summary>What this row asks the installer to do; the installer decides what is worth writing.</summary>
    public InstallChoice ToChoice() => new(
        Candidate,
        Target?.InternalName,
        Name.Trim(),
        DisplayName: NullIfEmpty(DisplayName.Trim()),
        Author: NullIfEmpty(Author.Trim()),
        ModUrl: NullIfEmpty(ModUrl.Trim()),
        Notes: NullIfEmpty(Notes.Trim()),
        PreviewImage: _chosenPicture,
        Version: NullIfEmpty(Version.Trim()),
        Description: NullIfEmpty(Description.Trim()),
        GameBanana: GameBanana);

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    partial void OnTargetChanged(CharacterChoiceViewModel? value)
    {
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(DestinationText));
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DestinationText));
}

/// <summary>The mod installer: what a source contains, where each mod would go, and why, before copying.</summary>
/// <remarks>Disposing the plan on close removes whatever was unpacked.</remarks>
public sealed partial class ModInstallViewModel(
    GameContext game,
    IModInstaller installer,
    IAppSettingsStore settings,
    INotificationService notifications,
    ViewModelWorkRunner work,
    ITextCatalogue text,
    IClipboardImageReader clipboard,
    IClipboardTextReader clipboardText,
    IStoragePicker picker,
    IPictureDownloader downloader,
    IGameBananaClient gameBanana,
    IGameBananaInstallSource gameBananaSource,
    IUrlLauncher urls,
    IDownloadManager downloads,
    IModCopies copies,
    IUiDispatcher ui,
    ILogger logger,
    Func<CancellationToken, Task> rescan,
    Func<string, bool> goToMod) : ObservableObject, IDisposable
{
    private readonly GameContext _game = game;
    private readonly IModInstaller _installer = installer;
    private readonly IAppSettingsStore _settings = settings;
    private readonly INotificationService _notifications = notifications;
    private readonly ViewModelWorkRunner _work = work;
    private readonly IClipboardImageReader _clipboard = clipboard;
    private readonly IClipboardTextReader _clipboardText = clipboardText;
    private readonly IStoragePicker _picker = picker;
    private readonly IPictureDownloader _downloader = downloader;
    private readonly IGameBananaClient _gameBanana = gameBanana;
    private readonly IGameBananaInstallSource _gameBananaSource = gameBananaSource;
    private readonly IUrlLauncher _urls = urls;
    private readonly IDownloadManager _downloads = downloads;
    private readonly IModCopies _copies = copies;
    private readonly Func<string, bool> _goToMod = goToMod;
    private readonly IUiDispatcher _ui = ui;
    private readonly ILogger _logger = logger;
    private readonly ITextCatalogue _text = text;
    private readonly Func<CancellationToken, Task> _rescan = rescan;

    private InstallPlan? _plan;
    private GameBananaMod? _pending;
    private GameBananaFile? _pendingFile;
    private GameBananaMod? _choosingPage;
    private string? _askTargetVariantId;
    private PreviewImageSource? _pendingPicture;
    private DownloadJob? _job;
    private string? _downloadId;
    private CancellationTokenSource? _reading;
    private readonly Lock _settleGate = new();
    private DownloadJob? _settled;
    private Task _settling = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Whether the panel is on screen.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Whether the source is being read.</summary>
    [ObservableProperty]
    private bool _isReading;

    /// <summary>The source's own name, for the title.</summary>
    [ObservableProperty]
    private string? _sourceName;

    /// <summary>The character the user dropped onto, or null when they dropped on the grid.</summary>
    [ObservableProperty]
    private string? _targetName;

    /// <summary>One row per mod found.</summary>
    public ObservableCollection<InstallRowViewModel> Rows { get; } = [];

    /// <summary>Files in the source that belong to no mod and will not be installed.</summary>
    public ObservableCollection<string> StrandedFiles { get; } = [];

    /// <summary>The panel's title.</summary>
    public string Heading => IsListingDownloads
        ? _text[nameof(Strings.Downloads_Heading)]
        : IsAskingForAddress || IsChoosingFile
        ? _text[nameof(Strings.GameBanana_Prompt_Heading)]
        : IsAlreadyInstalled
        ? _text[nameof(Strings.GameBanana_Already_Heading)]
        : TargetName is { Length: > 0 } character
            ? _text.Format(nameof(Strings.ModInstall_Heading_Character), character)
            : _text[nameof(Strings.ModInstall_Heading)];

    /// <summary>Whether the line under the title has a count to give yet: only once a source was read.</summary>
    public bool HasSummary =>
        !IsReading && !IsDownloading && !IsAskingForAddress && !IsDownloadBlocked && !IsAlreadyInstalled
        && !IsChoosingFile && !IsListingDownloads;

    /// <summary>What was found, in one line.</summary>
    public string SummaryText => _text.Format(
        nameof(Strings.ModInstall_Summary), _text.Mods(Rows.Count), SourceName ?? string.Empty);

    /// <summary>The header on the stranded-files list.</summary>
    public string StrandedText =>
        _text.Format(nameof(Strings.ModInstall_Stranded), _text.Files(StrandedFiles.Count));

    /// <summary>How many rows are ticked.</summary>
    public int SelectedCount => Rows.Count(row => row.IsSelected);

    /// <summary>The label on the button that does it.</summary>
    public string InstallText => _text.Format(nameof(Strings.ModInstall_Install), _text.Mods(SelectedCount));

    /// <summary>Whether anything is ticked to install.</summary>
    public bool CanInstall => SelectedCount > 0 && !IsReading;

    /// <summary>Whether anything installable was found.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Whether any file will be left behind.</summary>
    public bool HasStranded => StrandedFiles.Count > 0;

    /// <summary>Whether the source held nothing that looks like a mod, once one was read.</summary>
    public bool IsEmpty =>
        !IsReading && !IsDownloading && !IsAskingForAddress && !IsDownloadBlocked && !IsAlreadyInstalled
        && !IsChoosingFile && !IsListingDownloads && Rows.Count == 0;

    // Every download at once

    /// <summary>The download list the panel shows every current download from; set by that list.</summary>
    public DownloadsViewModel? DownloadList { get; internal set; }

    /// <summary>Whether the panel is listing every download that is running or waiting to be installed.</summary>
    [ObservableProperty]
    private bool _isListingDownloads;

    /// <summary>Whether the panel offers <em>Download another…</em>: while a download runs, or while they are listed.</summary>
    public bool CanDownloadAnother => IsDownloading || IsListingDownloads;

    /// <summary>Opens the panel on every download that is running or waiting to be installed.</summary>
    public void ListDownloads()
    {
        Close();
        IsListingDownloads = true;
        IsOpen = true;
        RaiseCounts();
    }

    /// <summary>Leaves whatever is downloading running and asks for another mod's address, for the same character.</summary>
    [RelayCommand]
    private void DownloadAnother()
    {
        var target = _job?.Record.TargetVariantId;
        var name = _job?.Record.TargetDisplayName;
        AskForAddress(target, name);
    }

    // From a GameBanana address

    /// <summary>What the panel is doing while it talks to GameBanana, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameBananaNote))]
    private string? _gameBananaNote;

    /// <summary>Whether that note is worth drawing: not while the progress bar says the same.</summary>
    public bool HasGameBananaNote => !IsDownloading && GameBananaNote is { Length: > 0 };

    /// <summary>The page to open in a browser because GameBanana would not hand the archive over, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloadBlocked))]
    private string? _blockedPageUrl;

    /// <summary>Whether the download was answered with a browser challenge.</summary>
    public bool IsDownloadBlocked => BlockedPageUrl is { Length: > 0 };

    /// <summary>Opens the page the download was blocked on, in the user's browser.</summary>
    [RelayCommand]
    public Task OpenBlockedPageAsync() => BlockedPageUrl is { Length: > 0 } address
        ? _urls.OpenAsync(address, CancellationToken.None)
        : Task.CompletedTask;

    // Which of the page's files

    /// <summary>The page's files, when it has several, to choose one before anything downloads.</summary>
    public GameBananaFileChooserViewModel FileChooser { get; } = new GameBananaFileChooserViewModel(text);

    /// <summary>Whether the panel is asking which of the page's files to download.</summary>
    [ObservableProperty]
    private bool _isChoosingFile;

    /// <summary>Downloads the file chosen, and then shows the card as for any other download.</summary>
    [RelayCommand]
    public void DownloadChosenFile()
    {
        if (!IsChoosingFile || _choosingPage is not { } page || FileChooser.Selected?.File is not { } file)
        {
            return;
        }

        _choosingPage = null;
        IsChoosingFile = false;
        FileChooser.Clear();
        RaiseCounts();

        StartDownload(page, file);
    }

    // The download itself

    /// <summary>Completes when the panel has finished reacting to a download that has just stopped.</summary>
    /// <returns>A task that completes when the card, or the reason, is on screen.</returns>
    public Task WhenSettledAsync() => _settling;

    /// <summary>Which download this panel is showing, or null.</summary>
    public string? AttachedDownloadId
    {
        get => _downloadId;
        private set => SetProperty(ref _downloadId, value);
    }

    /// <summary>Whether a download is running and this panel is watching it.</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>What is being downloaded.</summary>
    [ObservableProperty]
    private string? _downloadName;

    /// <summary>How far along, out of 100.</summary>
    [ObservableProperty]
    private double _downloadPercent;

    /// <summary>Whether the bar has to sweep rather than fill, because nobody said how big the file is.</summary>
    [ObservableProperty]
    private bool _isDownloadUnmeasured;

    /// <summary>How much has arrived, against how much there is.</summary>
    [ObservableProperty]
    private string? _downloadProgressText;

    /// <summary>How fast it is going, or null before that can be said.</summary>
    [ObservableProperty]
    private string? _downloadRateText;

    /// <summary>How much longer, or null when that cannot be estimated.</summary>
    [ObservableProperty]
    private string? _downloadRemainingText;

    // Already have it

    /// <summary>The mod asked for, while the panel is saying it is already installed.</summary>
    private long? _alreadyModId;

    /// <summary>Whether the panel is saying the mod asked for is already installed, and asking what to do.</summary>
    [ObservableProperty]
    private bool _isAlreadyInstalled;

    /// <summary>Every installed copy of the mod asked for, each with a way to go to it.</summary>
    public ObservableCollection<InstalledCopyViewModel> InstalledCopies { get; } = [];

    /// <summary>Downloads the mod anyway, to install a second copy beside the first.</summary>
    /// <returns>A task that completes when the card is on screen, or the reason is.</returns>
    [RelayCommand]
    public Task DownloadAnywayAsync()
    {
        if (_alreadyModId is not { } modId)
        {
            return Task.CompletedTask;
        }

        var target = _askTargetVariantId;
        var name = TargetName;

        return OpenFromGameBananaAsync(modId, target, name, CancellationToken.None, again: true);
    }

    /// <summary>Closes the panel and opens the page that holds an installed copy, with it selected.</summary>
    /// <param name="copy">The copy to go to.</param>
    [RelayCommand]
    public void GoToCopy(InstalledCopyViewModel? copy)
    {
        if (copy is null)
        {
            return;
        }

        Close();

        if (!_goToMod(copy.ModFolder))
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                _text[nameof(Strings.GameBanana_Already_Heading)],
                _text.Format(nameof(Strings.GameBanana_Already_Gone), copy.FolderName));
        }
    }

    private void ShowAlreadyInstalled(long modId, IReadOnlyList<InstalledCopy> copies)
    {
        _alreadyModId = modId;
        InstalledCopies.Clear();

        foreach (var copy in copies)
        {
            InstalledCopies.Add(new InstalledCopyViewModel(copy, WhereIs(copy)));
        }

        IsReading = false;
        GameBananaNote = null;
        IsAlreadyInstalled = true;
        RaiseCounts();
    }

    /// <summary>"under Xingqiu", or that it is filed under nobody.</summary>
    private string WhereIs(InstalledCopy copy)
    {
        if (copy.VariantFolderName is not { Length: > 0 } folder)
        {
            return _text[nameof(Strings.GameBanana_Already_Unfiled)];
        }

        var character = _game.Data?.Variants.FirstOrDefault(variant =>
            string.Equals(variant.ModFilesName, folder, StringComparison.OrdinalIgnoreCase));

        return _text.Format(nameof(Strings.GameBanana_Already_Under), character?.DisplayName ?? folder);
    }

    /// <summary>Whether the panel is waiting for a GameBanana address to be typed or pasted.</summary>
    [ObservableProperty]
    private bool _isAskingForAddress;

    /// <summary>The address being typed into the prompt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitAddress))]
    private string _addressInput = string.Empty;

    /// <summary>Why the address in the box will not do, or null.</summary>
    [ObservableProperty]
    private string? _addressProblem;

    /// <summary>Whether the box holds an address worth reading.</summary>
    public bool CanSubmitAddress => GameBananaUrl.FindModId(AddressInput) is not null;

    /// <summary>Opens the panel asking for a mod's address; nothing is fetched until <em>Get it</em>.</summary>
    /// <param name="targetVariantId">A character to file it under, overriding the sorter, or null.</param>
    /// <param name="targetDisplayName">That character's name, for the title.</param>
    /// <param name="address">An address to put in the box already, or null for an empty one.</param>
    public void AskForAddress(
        string? targetVariantId, string? targetDisplayName, string? address = null)
    {
        Close();

        _askTargetVariantId = targetVariantId;
        TargetName = targetDisplayName;
        AddressInput = address ?? string.Empty;
        AddressProblem = null;
        IsAskingForAddress = true;
        IsOpen = true;
        RaiseCounts();
    }

    /// <summary>A paste with nothing focused: a GameBanana address opens the prompt with it in the box.</summary>
    /// <param name="targetVariantId">A character to file it under, or null to let auto-sort decide.</param>
    /// <param name="targetDisplayName">That character's name, for the title.</param>
    /// <param name="cancellationToken">Cancels reading the clipboard.</param>
    /// <returns>A task that completes when the prompt is up, or the reason it is not is.</returns>
    public async Task PasteAddressAsync(
        string? targetVariantId, string? targetDisplayName, CancellationToken cancellationToken)
    {
        var pasted = await _clipboardText.ReadAsync(cancellationToken).ConfigureAwait(true);

        if (GameBananaUrl.FindModId(pasted) is not { } modId)
        {
            _notifications.Add(
                NotificationSeverity.Information,
                _text[nameof(Strings.GameBanana_Prompt_Heading)],
                _text[nameof(Strings.GameBanana_Paste_NotFound)]);

            return;
        }

        AskForAddress(targetVariantId, targetDisplayName, GameBananaUrl.ForMod(modId));
    }

    /// <summary>Reads the address in the prompt and gets on with the install.</summary>
    /// <returns>A task that completes when the card is on screen, or the reason is.</returns>
    [RelayCommand]
    public Task SubmitAddressAsync()
    {
        if (GameBananaUrl.FindModId(AddressInput) is not { } modId)
        {
            AddressProblem = GameBananaUrl.IsFileAddress(AddressInput)
                ? _text[nameof(Strings.GameBanana_Prompt_FileAddress)]
                : _text[nameof(Strings.ModInstall_Url_NotGameBanana)];

            return Task.CompletedTask;
        }

        var target = _askTargetVariantId;
        var name = TargetName;
        IsAskingForAddress = false;

        return OpenFromGameBananaAsync(modId, target, name, CancellationToken.None);
    }

    /// <summary>Reads a GameBanana page, downloads its file and shows the ordinary install card, filled in.</summary>
    /// <remarks>A copy already running, listed or installed is used or asked about first.</remarks>
    /// <param name="modId">The mod id, as <see cref="GameBananaUrl.FindModId"/> read it.</param>
    /// <param name="targetVariantId">A character to file it under, overriding the sorter, or null.</param>
    /// <param name="targetDisplayName">That character's name, for the title.</param>
    /// <param name="cancellationToken">Cancels the look-up and the download.</param>
    /// <param name="again">True to download it even when installed or listed already.</param>
    /// <returns>A task that completes when the card is on screen, or the reason is.</returns>
    public async Task OpenFromGameBananaAsync(
        long modId,
        string? targetVariantId,
        string? targetDisplayName,
        CancellationToken cancellationToken,
        bool again = false)
    {
        if (_game.Data is null || _game.ModsDirectory is not { Length: > 0 })
        {
            return;
        }

        Reset();

        SourceName = GameBananaUrl.ForMod(modId);
        TargetName = targetDisplayName;
        _askTargetVariantId = targetVariantId;
        IsOpen = true;
        IsReading = true;
        RaiseCounts();

        if (!again)
        {
            var copies = await _copies
                .FindAsync(modId, _game.GameId, _game.Inventory, cancellationToken)
                .ConfigureAwait(true);

            if (copies.Download is { IsRunning: true } running)
            {
                IsReading = false;
                Attach(running);

                return;
            }

            if (copies.Download is { } ready)
            {
                IsReading = false;
                await OpenFromDownloadAsync(ready.Id, cancellationToken).ConfigureAwait(true);
                GameBananaNote = _text[nameof(Strings.GameBanana_Already_Downloaded)];

                return;
            }

            if (copies.IsInstalled)
            {
                ShowAlreadyInstalled(modId, copies.Installed);

                return;
            }
        }

        IsReading = true;
        GameBananaNote = _text[nameof(Strings.GameBanana_Prompt_Reading)];
        RaiseCounts();

        // The panel's own token, so Cancel really stops the page read.
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reading = reading;

        GameBananaMod? page = null;

        var ran = await _work.RunAsync(
            _text[nameof(Strings.GameBanana_Prompt_Heading)],
            async ct => page = await _gameBanana.GetModAsync(modId, cancellationToken: ct).ConfigureAwait(true),
            reading.Token).ConfigureAwait(true);

        _reading = null;
        IsReading = false;
        RaiseCounts();

        if (!ran || page is null)
        {
            GameBananaNote = null;
            IsOpen = false;
            Reset();
            RaiseCounts();

            return;
        }

        // Several files: the user chooses before anything downloads.
        if (page is { HasFileChoice: true, IsUnavailable: false })
        {
            GameBananaNote = null;
            _choosingPage = page;
            FileChooser.Show(page);
            IsChoosingFile = true;
            RaiseCounts();

            return;
        }

        StartDownload(page, file: null);
    }

    private void StartDownload(GameBananaMod page, GameBananaFile? file)
    {
        try
        {
            Attach(_downloads.Start(
                page,
                file,
                new DownloadRequest(_game.GameId, _askTargetVariantId, TargetName)));
        }
        catch (GameBananaException ex)
        {
            GameBananaNote = null;
            IsOpen = false;
            Reset();
            RaiseCounts();

            _notifications.Add(
                NotificationSeverity.Error, _text[nameof(Strings.GameBanana_Prompt_Heading)], ex.Message);
        }
    }

    /// <summary>Opens the panel on a running or finished download; the panel watches it, never owns it.</summary>
    public void Attach(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!ReferenceEquals(_job, job))
        {
            Detach();
        }

        _job = job;
        AttachedDownloadId = job.Id;
        IsListingDownloads = false;

        SourceName = job.Record.PageUrl ?? GameBananaUrl.ForMod(job.Record.ModId);
        TargetName = job.Record.TargetDisplayName;
        IsOpen = true;
        BlockedPageUrl = null;

        _downloads.Progress += OnDownloadProgress;
        _downloads.Changed += OnDownloadChanged;

        if (job.IsRunning)
        {
            IsDownloading = true;
            UpdateProgress(job);
            RaiseCounts();
        }

        RaiseCounts();

        // After subscribing, and idempotent: a download may finish in between and be seen both ways.
        if (!job.IsRunning)
        {
            Settle(job);
        }
    }

    /// <summary>Opens the panel on an entry from the download list, without asking GameBanana.</summary>
    /// <returns>A task that completes when the card is on screen, or the reason is.</returns>
    public async Task OpenFromDownloadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (_game.Data is null || _game.ModsDirectory is not { Length: > 0 })
        {
            return;
        }

        var contents = await _downloads.OpenAsync(id, cancellationToken).ConfigureAwait(true);

        if (contents is null)
        {
            _notifications.Add(
                NotificationSeverity.Warning,
                _text[nameof(Strings.Downloads_Heading)],
                _text[nameof(Strings.Downloads_Gone)]);

            return;
        }

        Detach();
        Reset();

        AttachedDownloadId = id;
        _pending = contents.Mod;
        _pendingFile = contents.File;
        _pendingPicture = contents.Picture;

        await OpenAsync(
                contents.ArchivePath,
                contents.Record.TargetVariantId,
                contents.Record.TargetDisplayName,
                cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Leaves the download running and takes the panel off the screen.</summary>
    [RelayCommand]
    public void HideDownload()
    {
        IsOpen = false;
        Detach();
        Reset();
        RaiseCounts();
    }

    /// <summary>Stops the download and closes the panel.</summary>
    [RelayCommand]
    public void CancelDownload()
    {
        if (_downloadId is { Length: > 0 } id)
        {
            _downloads.Cancel(id);
        }

        IsOpen = false;
        Detach();
        Reset();
        RaiseCounts();
    }

    private void Detach()
    {
        _downloads.Progress -= OnDownloadProgress;
        _downloads.Changed -= OnDownloadChanged;
        _job = null;
        IsDownloading = false;

        lock (_settleGate)
        {
            _settled = null;
        }
    }

    private void OnDownloadProgress(object? sender, DownloadChange change)
    {
        if (!ReferenceEquals(change.Job, _job))
        {
            return;
        }

        // The download reports from whichever thread is reading the socket.
        _ui.Post(() =>
        {
            if (ReferenceEquals(change.Job, _job))
            {
                UpdateProgress(change.Job);
            }
        });
    }

    private void OnDownloadChanged(object? sender, DownloadChange change)
    {
        if (!ReferenceEquals(change.Job, _job) || change.Job.IsRunning)
        {
            return;
        }

        _ui.Post(() =>
        {
            if (ReferenceEquals(change.Job, _job))
            {
                Settle(change.Job);
            }
        });
    }

    /// <summary>Reacts to a download having stopped, once and once only.</summary>
    private void Settle(DownloadJob job)
    {
        lock (_settleGate)
        {
            // Once, and only while this download is still the one shown.
            if (ReferenceEquals(_settled, job) || !ReferenceEquals(_job, job))
            {
                return;
            }

            _settled = job;
        }

        IsDownloading = false;
        _settling = FinishAsync(job);
    }

    private void UpdateProgress(DownloadJob job)
    {
        DownloadName = job.Record.Name ?? job.Record.FileName;
        DownloadPercent = job.Fraction is { } fraction ? fraction * 100 : 0;
        IsDownloadUnmeasured = job.Fraction is null;

        DownloadProgressText = job.TotalBytes is > 0
            ? _text.Format(
                nameof(Strings.Downloads_Progress),
                ByteSize.Describe(job.Bytes),
                ByteSize.Describe(job.TotalBytes.Value))
            : ByteSize.Describe(job.Bytes);

        DownloadRateText = job.BytesPerSecond is { } rate
            ? _text.Format(nameof(Strings.Downloads_Rate), ByteSize.Describe((long)rate))
            : null;

        DownloadRemainingText = job.Remaining is { } left
            ? _text.Format(nameof(Strings.Downloads_Remaining), ByteSize.DescribeDuration(left))
            : null;

        GameBananaNote = _text.Format(
            nameof(Strings.GameBanana_Prompt_Downloading), DownloadName ?? string.Empty);
    }

    private async Task FinishAsync(DownloadJob job)
    {
        var record = job.Record;

        switch (record.State)
        {
            case DownloadState.Ready:
                GameBananaNote = null;

                if (record.Error is { Length: > 0 } pictureError)
                {
                    _notifications.Add(
                        NotificationSeverity.Warning,
                        _text[nameof(Strings.GameBanana_Prompt_Heading)],
                        pictureError);
                }

                await OpenFromDownloadAsync(job.Id, CancellationToken.None).ConfigureAwait(true);

                return;

            case DownloadState.Blocked:
                _pending = job.Page ?? _pending;
                _pendingPicture = job.Picture;

                // Null on purpose: the file fetched in a browser may not be the one XXSM would have taken.
                _pendingFile = null;
                BlockedPageUrl = record.PageUrl;
                GameBananaNote = _text[nameof(Strings.GameBanana_Prompt_Blocked)];
                RaiseCounts();

                return;

            case DownloadState.Cancelled:
                IsOpen = false;
                Detach();
                Reset();
                RaiseCounts();

                return;

            default:
                GameBananaNote = record.Error;
                RaiseCounts();

                if (record.Error is { Length: > 0 } failure)
                {
                    _notifications.Add(
                        NotificationSeverity.Error,
                        _text[nameof(Strings.GameBanana_Prompt_Heading)],
                        failure);
                }

                return;
        }
    }

    /// <summary>Asks for a mod folder, or a folder of mods, and reads it as OpenAsync does.</summary>
    /// <param name="targetVariantId">The character to install into, which overrides the sorter, or null.</param>
    /// <param name="targetDisplayName">That character's name, for the title.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task PickFolderAsync(
        string? targetVariantId, string? targetDisplayName, CancellationToken cancellationToken)
    {
        var chosen = await _picker
            .PickFolderAsync(_text[nameof(Strings.ModInstall_Folder_PickerTitle)], cancellationToken: cancellationToken)
            .ConfigureAwait(true);

        if (chosen is { Length: > 0 })
        {
            await OpenAsync(chosen, targetVariantId, targetDisplayName, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Asks for a mod archive, in any format the reader opens, and reads it as OpenAsync does.</summary>
    /// <param name="targetVariantId">The character to install into, which overrides the sorter, or null.</param>
    /// <param name="targetDisplayName">That character's name, for the title.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task PickArchiveAsync(
        string? targetVariantId, string? targetDisplayName, CancellationToken cancellationToken)
    {
        var chosen = await _picker
            .PickFileAsync(
                _text[nameof(Strings.ModInstall_Archive_PickerTitle)],
                [new FileTypeFilter(_text[nameof(Strings.ModInstall_Archive_FileType)], IModArchiveReader.SupportedExtensions)],
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);

        if (chosen is { Length: > 0 })
        {
            await OpenAsync(chosen, targetVariantId, targetDisplayName, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Reads a folder or archive and shows what it would install.</summary>
    /// <param name="source">The folder or archive the user chose or dropped.</param>
    /// <param name="targetVariantId">A character they dropped onto, which overrides the sorter, or null.</param>
    /// <param name="targetDisplayName">That character's name, for the title.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task OpenAsync(
        string source,
        string? targetVariantId,
        string? targetDisplayName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (_game.Data is not { } data || _game.ModsDirectory is not { Length: > 0 } modsDirectory)
        {
            return;
        }

        // Taken before Reset, which clears it.
        var pending = _pending;
        var pendingPicture = _pendingPicture;
        var pendingFile = _pendingFile;
        var download = _downloadId;

        Reset();

        // The archive belongs to the download list, not to this panel.
        AttachedDownloadId = download;
        _pendingFile = pendingFile;
        SourceName = Path.GetFileName(PathComparer.Normalize(source).TrimEnd('/'));
        TargetName = targetDisplayName;
        IsOpen = true;
        IsReading = true;
        RaiseCounts();

        var targets = Choices(data);

        await _work.RunAsync(
            Heading,
            async ct =>
            {
                var saved = await _settings.ReadAsync(ct).ConfigureAwait(true);

                var plan = await _installer
                    .PlanAsync(
                        source, data, modsDirectory, targetVariantId, saved.Sort.ToSortSettings(), ct)
                    .ConfigureAwait(true);

                _plan = plan;

                // One mod shows its files open; several start folded.
                var expandFiles = plan.Candidates.Count == 1;

                var single = plan.Candidates.Count == 1;
                var fill = pending is not null && saved.GameBanana.FetchOnInstallOrDefault;

                foreach (var candidate in plan.Candidates)
                {
                    var row = new InstallRowViewModel(
                        candidate,
                        targets,
                        _text,
                        _clipboard,
                        _picker,
                        _downloader,
                        _gameBanana,
                        _settings,
                        _logger,
                        expandFiles);

                    if (pending is { } page)
                    {
                        row.GameBanana = _gameBananaSource.ProvenanceOf(page, pendingFile);
                        row.ModUrl = page.PageUrl.AbsoluteUri;

                        if (fill)
                        {
                            row.Author = page.Author ?? row.Author;
                            row.Version = page.Version ?? row.Version;
                            row.Description = GameBananaText.ForStorage(page.Description) ?? row.Description;

                            if (single)
                            {
                                row.DisplayName = page.Name ?? row.DisplayName;

                                if (pendingPicture is { } picture)
                                {
                                    await row.UseFetchedPictureAsync(picture).ConfigureAwait(true);
                                }
                            }
                        }
                    }

                    row.PropertyChanged += OnRowChanged;
                    Rows.Add(row);
                }

                foreach (var file in plan.StrandedFiles)
                {
                    StrandedFiles.Add(file);
                }

                foreach (var diagnostic in plan.Diagnostics)
                {
                    _notifications.Add(NotificationSeverity.Information, Heading, diagnostic.Message);
                }
            },
            cancellationToken).ConfigureAwait(true);

        IsReading = false;
        RaiseCounts();
    }

    /// <summary>Copies in the mods whose rows are still ticked.</summary>
    [RelayCommand]
    private Task InstallAsync()
    {
        if (_plan is not { } plan || _game.Data is not { } data || SelectedCount == 0)
        {
            return Task.CompletedTask;
        }

        var choices = Rows.Where(row => row.IsSelected).Select(row => row.ToChoice()).ToList();

        return _work.RunAsync(
            Heading,
            async ct =>
            {
                var result = await _installer.ApplyAsync(plan, choices, data, ct).ConfigureAwait(true);

                // Before Close, which forgets which download this came from.
                if (_downloadId is { Length: > 0 } download)
                {
                    await _downloads
                        .MarkInstalledAsync(
                            download,
                            result.Outcomes.FirstOrDefault(outcome => outcome.Succeeded)?.InstalledPath,
                            ct)
                        .ConfigureAwait(true);
                }

                Close();
                await _rescan(ct).ConfigureAwait(true);

                _notifications.Add(
                    result.Failures.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Information,
                    _text[nameof(Strings.ModInstall_Heading)],
                    _text.Format(nameof(Strings.ModInstall_Done), _text.Mods(result.InstalledCount)));

                foreach (var failure in result.Failures)
                {
                    _notifications.Add(
                        NotificationSeverity.Error, failure.Choice.Candidate.Name, failure.Error!);
                }

                foreach (var partial in result.MetadataFailures)
                {
                    _notifications.Add(
                        NotificationSeverity.Warning,
                        _text[nameof(Strings.ModInstall_Heading)],
                        _text.Format(
                            nameof(Strings.ModInstall_MetadataFailed),
                            partial.Choice.Candidate.Name,
                            partial.MetadataError!));
                }
            },
            CancellationToken.None);
    }

    /// <summary>Closes the panel, discarding what was unpacked; a running download is not stopped.</summary>
    [RelayCommand]
    public void Close()
    {
        _reading?.Cancel();

        IsOpen = false;
        Detach();
        Reset();
        RaiseCounts();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reading?.Cancel();
        Detach();
        Reset();
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<CharacterChoiceViewModel> Choices(Packs.Merge.GameData data) =>
    [
        .. data.VisibleVariants
            .Select(variant => new CharacterChoiceViewModel(
                variant.InternalName, variant.DisplayName, variant.ModFilesName))
            .OrderBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase),
    ];

    private void Reset()
    {
        var rows = Rows.ToList();

        foreach (var row in rows)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Rows.Clear();

        // After the rows leave the screen, never before: disposing a drawn bitmap crashes the renderer.
        foreach (var row in rows)
        {
            row.Dispose();
        }
        StrandedFiles.Clear();

        // Disposing the plan deletes the staging the rows point at, so only once they are gone.
        _plan?.Dispose();
        _plan = null;
        _pending = null;
        _pendingFile = null;
        _pendingPicture = null;
        AttachedDownloadId = null;

        SourceName = null;
        TargetName = null;
        GameBananaNote = null;
        BlockedPageUrl = null;
        IsAskingForAddress = false;
        IsListingDownloads = false;
        IsAlreadyInstalled = false;
        _alreadyModId = null;
        InstalledCopies.Clear();
        IsChoosingFile = false;
        _choosingPage = null;
        FileChooser.Clear();
        AddressProblem = null;
        IsDownloading = false;
        DownloadName = null;
        DownloadPercent = 0;
        IsDownloadUnmeasured = false;
        DownloadProgressText = null;
        DownloadRateText = null;
        DownloadRemainingText = null;
        _askTargetVariantId = null;
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallRowViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(InstallText));
            OnPropertyChanged(nameof(CanInstall));
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(StrandedText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasStranded));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSummary));
    }

    partial void OnIsListingDownloadsChanged(bool value)
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanDownloadAnother));
    }

    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadAnother));
        OnPropertyChanged(nameof(HasGameBananaNote));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnIsReadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSummary));
    }

    partial void OnIsAskingForAddressChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(HasSummary));
    }

    partial void OnBlockedPageUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSummary));
    }

    partial void OnSourceNameChanged(string? value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnTargetNameChanged(string? value) => OnPropertyChanged(nameof(Heading));
}
