using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Xxsm.Core.Serialization;
using Xxsm.Core.Settings;

namespace Xxsm.Core.GameBanana;

/// <summary>The default <see cref="IGameBananaClient"/>.</summary>
/// <remarks>
/// The <c>HttpClient</c> must have no timeout of its own: this class times metadata and downloads differently.
/// Settings are read on every call, so the master switch and the rate apply at once.
/// </remarks>
public sealed class GameBananaClient : IGameBananaClient
{
    /// <summary>The largest download taken when GameBanana gives no size: 4 GB.</summary>
    private const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>The largest JSON answer read: 8 MB.</summary>
    private const long MaxAnswerBytes = 8L * 1024 * 1024;

    /// <summary>How long a metadata request may take.</summary>
    public static TimeSpan MetadataTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a download may take.</summary>
    public static TimeSpan DownloadTimeout { get; } = TimeSpan.FromMinutes(30);

    /// <summary>How many times a refused request is tried altogether.</summary>
    public const int MaxAttempts = 4;

    /// <summary>How many bytes a download reports progress in.</summary>
    private const long ProgressStep = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IGameBananaCache _cache;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger _logger;
    private readonly RequestPacer _pacer;
    private readonly Func<double> _jitter;

    /// <summary>Creates the client.</summary>
    /// <param name="http">The client to use, with its <c>User-Agent</c> set and no timeout.</param>
    /// <param name="cache">The on-disk response cache.</param>
    /// <param name="settings">Read on every call for the master switch, the rate and the API address.</param>
    /// <param name="logger">Structured log sink.</param>
    /// <param name="time">Supplies "now" for the pacer.</param>
    /// <param name="delay">How to wait between requests.</param>
    /// <param name="jitter">Supplies the 0..1 spread applied to a backoff; <c>Random.Shared</c> when null.</param>
    public GameBananaClient(
        HttpClient http,
        IGameBananaCache cache,
        IAppSettingsStore settings,
        ILogger logger,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _cache = cache;
        _settings = settings;
        _logger = logger.ForContext<GameBananaClient>();
        _pacer = new RequestPacer(time, delay);
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
    }

    /// <inheritdoc />
    public async Task<GameBananaMod> GetModAsync(
        long modId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(modId);

        var options = await ReadOptionsAsync(modId, cancellationToken).ConfigureAwait(false);

        var profileBody = await BodyAsync(
            IGameBananaCache.ProfileKey(modId),
            $"Mod/{modId.ToString(CultureInfo.InvariantCulture)}/ProfilePage",
            modId,
            options,
            refresh,
            cancellationToken).ConfigureAwait(false);

        var profile = Deserialise(profileBody, CoreJsonContext.Default.GameBananaProfilePage, modId)
            ?? throw new GameBananaException(
                $"GameBanana returned nothing at all for mod {modId.ToString(CultureInfo.InvariantCulture)}.",
                modId);

        IReadOnlyList<GameBananaFile>? files = null;

        if ((profile.Files?.Count ?? 0) == 0 && profile is { IsTrashed: not true, IsWithheld: not true })
        {
            var downloadBody = await BodyAsync(
                IGameBananaCache.DownloadKey(modId),
                $"Mod/{modId.ToString(CultureInfo.InvariantCulture)}/DownloadPage",
                modId,
                options,
                refresh,
                cancellationToken).ConfigureAwait(false);

            files = Deserialise(downloadBody, CoreJsonContext.Default.GameBananaDownloadPage, modId)?.Files;
        }

        var mod = GameBananaMod.From(profile, modId, files);

        _logger.Information(
            "Read GameBanana mod {ModId}: {Name} by {Author}, {Files} file(s)",
            mod.ModId,
            mod.Name ?? "(unnamed)",
            mod.Author ?? "(unknown)",
            mod.Files.Count);

        return mod;
    }

    /// <inheritdoc />
    public async Task<GameBananaDownloadedFile> DownloadAsync(
        GameBananaFile file,
        string destinationDirectory,
        long? modId = null,
        IProgress<GameBananaDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var options = await ReadOptionsAsync(modId, cancellationToken).ConfigureAwait(false);

        var address = GameBananaMod.AddressOf(file)
            ?? throw new GameBananaException(
                "That file has no download address on its GameBanana page, so there is nothing to fetch.",
                modId);

        var name = FileNameFor(file, address);
        var destination = Path.Combine(destinationDirectory, name);
        var part = destination + ".part";

        Directory.CreateDirectory(destinationDirectory);

        using var response = await SendAsync(address, options, modId, DownloadTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (IsChallenge(response))
        {
            throw Blocked(response, address, modId);
        }

        // Only a suspicion: the first bytes decide, since no archive starts with '<' and every web page does.
        var labelledAsPage = response.Content.Headers.ContentType?.MediaType is { } type
                             && type.Contains("html", StringComparison.OrdinalIgnoreCase);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        var total = response.Content.Headers.ContentLength ?? file.Filesize;
        long written = 0;

        var ceiling = total is > 0 and var said ? said + Math.Max(1024 * 1024, said / 20) : MaxDownloadBytes;

        try
        {
            string actual;

            await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var buffer = new byte[128 * 1024];
                var reported = 0L;
                int read;

                while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (written == 0 && labelledAsPage && StartsWithMarkup(buffer.AsSpan(0, read)))
                    {
                        throw Blocked(response, address, modId);
                    }

                    if (written + read > ceiling)
                    {
                        throw new GameBananaException(
                            $"'{name}' is larger than GameBanana said it would be ({Megabytes(ceiling)}), so the download " +
                            "was stopped. Nothing has been installed.",
                            modId,
                            address);
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    digest.AppendData(buffer, 0, read);
                    written += read;

                    if (written - reported >= ProgressStep)
                    {
                        reported = written;
                        progress?.Report(new GameBananaDownloadProgress(written, total));
                    }
                }

                progress?.Report(new GameBananaDownloadProgress(written, total ?? written));

                actual = Convert.ToHexStringLower(digest.GetCurrentHash());
            }

            var expected = file.Md5Checksum?.Trim();

            if (expected is { Length: > 0 } && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new GameBananaException(
                    $"'{name}' did not arrive intact: GameBanana says its checksum is {expected}, " +
                    $"and what downloaded came to {actual}. Nothing has been installed. Try again, " +
                    "or fetch it in your browser and drop the file in.",
                    modId,
                    address);
            }

            // Renamed only once closed and flushed, so no bytes arrive after the rename.
            File.Move(part, destination, overwrite: true);

            _logger.Information(
                "Downloaded GameBanana file {FileId} to {Path}: {Bytes} bytes, checksum {Checksum}",
                file.IdRow,
                destination,
                written,
                expected is { Length: > 0 } ? "verified" : "not published");

            return new GameBananaDownloadedFile(destination, written, expected, expected is { Length: > 0 });
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Discard(part);

            // The download's own time limit, not the user's cancel: said as a failure, not silently dropped.
            throw new GameBananaException(
                $"'{name}' stopped part-way: it did not finish within {Duration(DownloadTimeout)}. Nothing has " +
                "been installed. Try again, or fetch it in your browser and drop the file in.",
                modId,
                address,
                ex);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            Discard(part);

            throw new GameBananaException(
                $"'{name}' stopped part-way: {ex.Message} Nothing has been installed. Try again, or fetch it " +
                "in your browser and drop the file in.",
                modId,
                address,
                ex);
        }
        catch
        {
            Discard(part);

            throw;
        }
    }

    /// <inheritdoc />
    public async Task WaitForTurnAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

        await _pacer.WaitAsync(settings.GameBanana.RequestGap, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> ForgetAsync(long? modId = null, CancellationToken cancellationToken = default) =>
        _cache.ForgetAsync(modId, cancellationToken);

    private static string Megabytes(long bytes) =>
        $"{(bytes / (1024.0 * 1024.0)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} MB";

    private async Task<GameBananaSettings> ReadOptionsAsync(long? modId, CancellationToken cancellationToken)
    {
        var settings = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);

        return settings.GameBanana.Enabled
            ? settings.GameBanana
            : throw new GameBananaDisabledException(modId);
    }

    private async Task<string> BodyAsync(
        string cacheKey,
        string path,
        long modId,
        GameBananaSettings options,
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (!refresh)
        {
            var cached = await _cache
                .ReadAsync(cacheKey, options.CacheLifetime, cancellationToken).ConfigureAwait(false);

            if (cached is not null)
            {
                _logger.Debug("Served GameBanana {Path} for mod {ModId} from the cache", path, modId);

                return cached;
            }
        }

        var address = new Uri(options.ApiBase, path);

        using var response = await SendAsync(address, options, modId, MetadataTimeout, cancellationToken)
            .ConfigureAwait(false);

        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reading.CancelAfter(MetadataTimeout);

        var mediaType = response.Content.Headers.ContentType?.MediaType;

        if (IsChallenge(response))
        {
            throw NotData(response, body: null, address, modId);
        }

        string body;

        try
        {
            if (response.Content.Headers.ContentLength > MaxAnswerBytes)
            {
                throw new GameBananaException(
                    $"gamebanana.com's answer is larger than {MaxAnswerBytes / (1024 * 1024)} MB, far more than a mod's page; it was not read.",
                    modId,
                    address);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(reading.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;

            while ((read = await stream.ReadAsync(chunk, reading.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxAnswerBytes)
                {
                    throw new GameBananaException(
                        $"gamebanana.com's answer is larger than {MaxAnswerBytes / (1024 * 1024)} MB, far more than a mod's page; it was not read.",
                        modId,
                        address);
                }

                buffer.Write(chunk, 0, read);
            }

            body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            throw new GameBananaException(
                $"Could not read gamebanana.com's answer: {ex.Message} Everything else in XXSM still works.",
                modId,
                address,
                ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // This read's own time limit, not the caller's cancel: a failure.
            throw new GameBananaException(
                $"Could not read gamebanana.com's answer: {Describe(ex, MetadataTimeout)} " +
                "Everything else in XXSM still works.",
                modId,
                address,
                ex);
        }

        // Judged by what arrived, not its label: GameBanana's cache serves good data labelled text/html.
        if (!LooksLikeData(body))
        {
            throw NotData(response, body, address, modId);
        }

        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Debug(
                "GameBanana labelled its answer for {Address} {MediaType} (cache {CacheStatus}); it held data, so it was used",
                address,
                mediaType,
                HeaderOf(response, "cf-cache-status") ?? "(none)");
        }

        await _cache.WriteAsync(cacheKey, body, cancellationToken).ConfigureAwait(false);

        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri address,
        GameBananaSettings options,
        long? modId,
        TimeSpan attemptTimeout,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _pacer.WaitAsync(options.RequestGap, cancellationToken).ConfigureAwait(false);

            HttpStatusCode status;
            string? reason;
            TimeSpan? retryAfter;

            // A timeout per attempt, not per call.
            using var attemptTimeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeoutSource.CancelAfter(attemptTimeout);

            try
            {
                var response = await _http
                    .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, attemptTimeoutSource.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode || IsChallenge(response))
                {
                    return response;
                }

                status = response.StatusCode;
                reason = response.ReasonPhrase;
                retryAfter = RetryAfterOf(response);
                response.Dispose();
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt >= MaxAttempts)
                {
                    throw new GameBananaException(
                        $"Could not reach gamebanana.com after " +
                        $"{MaxAttempts.ToString(CultureInfo.InvariantCulture)} attempts: {Describe(ex, attemptTimeout)} " +
                        "Everything else in XXSM still works.",
                        modId,
                        address,
                        ex);
                }

                var pause = RequestPacer.BackoffFor(attempt, null, _jitter());

                _logger.Warning(
                    ex,
                    "Could not reach GameBanana at {Address}; waiting {Seconds:0.0}s before attempt {Next} of {Max}",
                    address,
                    pause.TotalSeconds,
                    attempt + 1,
                    MaxAttempts);

                _pacer.Hold(pause);

                continue;
            }

            // Asked to wait longer than the backoff ceiling: every request is held for that long, and this one fails
            // now.
            if (retryAfter is { } asked && asked > RequestPacer.BackoffCeiling)
            {
                _pacer.Hold(asked);
                _logger.Warning(
                    "GameBanana answered {Status} for {Address} and asked XXSM to wait {Seconds:0}s; holding every request that long",
                    (int)status,
                    address,
                    asked.TotalSeconds);

                throw new GameBananaException(
                    $"GameBanana asked XXSM to wait {WaitWords(asked)} before asking again, so it will. Everything " +
                    "else in XXSM still works.",
                    modId,
                    address);
            }

            if (!ShouldRetry(status) || attempt >= MaxAttempts)
            {
                throw Refused(status, reason, address, modId, attempt);
            }

            var wait = RequestPacer.BackoffFor(attempt, retryAfter, _jitter());

            _logger.Warning(
                "GameBanana answered {Status} for {Address}; waiting {Seconds:0.0}s before attempt {Next} of {Max}",
                (int)status,
                address,
                wait.TotalSeconds,
                attempt + 1,
                MaxAttempts);

            _pacer.Hold(wait);
        }
    }

    private static string WaitWords(TimeSpan wait) => wait.TotalMinutes >= 1.5
        ? $"{Math.Round(wait.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minutes"
        : $"{Math.Round(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds";

    private static string Describe(Exception ex, TimeSpan attemptTimeout) => ex is OperationCanceledException
        ? $"it did not answer within {Duration(attemptTimeout)}."
        : ex.Message;

    private static string Duration(TimeSpan limit)
    {
        var (count, unit) = limit.TotalSeconds >= 60 && limit.TotalSeconds % 60 == 0
            ? ((int)limit.TotalMinutes, "minute")
            : ((int)limit.TotalSeconds, "second");

        return $"{count.ToString(CultureInfo.InvariantCulture)} {unit}{(count == 1 ? string.Empty : "s")}";
    }

    private GameBananaDownloadBlockedException Blocked(HttpResponseMessage response, Uri address, long? modId)
    {
        LogRefusal(response, address, body: null);

        return new GameBananaDownloadBlockedException(
            "GameBanana wants a browser to fetch this one — it answered with a web page instead of " +
            "the archive. Everything it told XXSM about the mod has been kept: open the page, " +
            "download the file yourself, and drop it in.",
            modId is { } id ? new Uri(GameBananaUrl.ForMod(id)) : address,
            modId);
    }

    /// <summary>Why an answer that should have been the mod's details was not, in words.</summary>
    private GameBananaException NotData(HttpResponseMessage response, string? body, Uri address, long? modId)
    {
        LogRefusal(response, address, body);

        if (IsChallenge(response))
        {
            return new GameBananaException(
                "GameBanana is asking for a browser check before it will answer — protection the " +
                "site puts in front of everyone, not a limit on XXSM. Mod management is unaffected; " +
                "try the look-up again in a few minutes.",
                modId,
                address);
        }

        if (body is not null && StartsWithMarkup(body.AsSpan()))
        {
            return new GameBananaException(
                "gamebanana.com answered with a web page" +
                (TitleOf(body) is { } title ? $" (\"{title}\")" : string.Empty) +
                " instead of the mod's details. Mod management is unaffected; try the look-up again " +
                "in a few minutes.",
                modId,
                address);
        }

        return new GameBananaException(
            $"gamebanana.com answered with {response.Content.Headers.ContentType?.MediaType ?? "something"} " +
            "that is not the mod's details. Mod management is unaffected; try the look-up again in a few minutes.",
            modId,
            address);
    }

    /// <summary>Logs everything that tells a challenge from a mislabelled answer.</summary>
    private void LogRefusal(HttpResponseMessage response, Uri address, string? body) =>
        _logger.Warning(
            "GameBanana answered {Address} with {Status} {MediaType} (cache {CacheStatus}, " +
            "challenge {Challenge}, page title {Title}), which is not what was asked for",
            address,
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType ?? "(no type)",
            HeaderOf(response, "cf-cache-status") ?? "(none)",
            HeaderOf(response, "cf-mitigated") ?? "(none)",
            body is null ? "(not read)" : TitleOf(body) ?? "(none)");

    /// <summary>Whether Cloudflare, in front of the site, says this is a browser challenge.</summary>
    private static bool IsChallenge(HttpResponseMessage response) =>
        string.Equals(HeaderOf(response, "cf-mitigated"), "challenge", StringComparison.OrdinalIgnoreCase);

    private static string? HeaderOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Whether a body is JSON by its first character, whatever it was labelled.</summary>
    private static bool LooksLikeData(string body)
    {
        var start = body.AsSpan().TrimStart().TrimStart('\uFEFF');

        return start.Length > 0 && start[0] is '{' or '[';
    }

    private static bool StartsWithMarkup(ReadOnlySpan<char> text)
    {
        var start = text.TrimStart().TrimStart('\uFEFF');

        return start.Length > 0 && start[0] == '<';
    }

    private static bool StartsWithMarkup(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("\uFEFF"u8))
        {
            bytes = bytes[3..];
        }

        var start = bytes.TrimStart(" \t\r\n"u8);

        return start.Length > 0 && start[0] == (byte)'<';
    }

    private static string? TitleOf(string body)
    {
        var open = body.IndexOf("<title", StringComparison.OrdinalIgnoreCase);

        var from = open < 0 ? -1 : body.IndexOf('>', open);

        if (from < 0)
        {
            return null;
        }

        var close = body.IndexOf("</title", from, StringComparison.OrdinalIgnoreCase);

        if (close < 0)
        {
            return null;
        }

        var title = System.Net.WebUtility.HtmlDecode(body[(from + 1)..close]).Trim();

        return title.Length is > 0 and <= 120 ? title : null;
    }

    private static GameBananaException Refused(
        HttpStatusCode status, string? reason, Uri address, long? modId, int attempts) => status switch
        {
            HttpStatusCode.NotFound => new GameBananaException(
                modId is { } id
                    ? $"There is no mod {id.ToString(CultureInfo.InvariantCulture)} on GameBanana — check the address."
                    : $"GameBanana has nothing at '{address}'.",
                modId,
                address),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new GameBananaException(
                $"GameBanana refused the request ({(int)status} {reason}). That mod may be private or withheld.",
                modId,
                address),
            _ => new GameBananaException(
                $"GameBanana answered {(int)status} {reason} after " +
                $"{attempts.ToString(CultureInfo.InvariantCulture)} attempt(s). Mod management is " +
                "unaffected; try again later.",
                modId,
                address),
        };

    private static bool ShouldRetry(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
        || (int)status >= 500;

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;

        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta;
        }

        return header.Date is { } date && date > DateTimeOffset.UtcNow
            ? date - DateTimeOffset.UtcNow
            : null;
    }

    private static string FileNameFor(GameBananaFile file, Uri address)
    {
        var candidate = file.File?.Trim();

        if (candidate is not { Length: > 0 })
        {
            candidate = Path.GetFileName(address.AbsolutePath);
        }

        if (candidate is not { Length: > 0 })
        {
            candidate = "download";
        }

        // A name and nothing else: '/', '\' and '..' could write outside the chosen folder.
        var safe = new string([
            .. Path.GetFileName(candidate.Replace('\\', '/'))
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '\\' ? '_' : c),
        ]);

        if (safe.Trim('.', ' ') is not { Length: > 0 } cleaned)
        {
            return "download";
        }

        // A Windows device name (NUL, CON, COM1…) is no file there.
        var stem = cleaned.Split('.')[0];

        return WindowsDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase) ? "_" + cleaned : cleaned;
    }

    private static readonly string[] WindowsDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private void Discard(string part)
    {
        try
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(ex, "Could not remove the abandoned download {Path}", part);
        }
    }

    private static T? Deserialise<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, long modId)
    {
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException ex)
        {
            throw new GameBananaException(
                $"GameBanana's answer for mod {modId.ToString(CultureInfo.InvariantCulture)} was not " +
                $"readable: {ex.Message}",
                modId,
                innerException: ex);
        }
    }
}
