using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xxsm.Core;
using Xxsm.Packs.Model;
using Xxsm.Packs.Serialization;

namespace Xxsm.Packs.Updates;

/// <summary>What the newest published XXSM is, against the one running.</summary>
/// <param name="Running">The running version, such as <c>1.0.0</c>.</param>
/// <param name="Latest">The newest release's version, without a leading <c>v</c>.</param>
/// <param name="Page">The release's page, where the downloads are.</param>
public sealed record AppUpdateResult(string Running, string Latest, Uri Page)
{
    /// <summary>Whether the newest release is newer than the running version.</summary>
    public bool IsNewer => AppVersionRequirement.IsOlderThan(Running, Latest);
}

/// <summary>Asks the project's releases which XXSM is the newest. It only reads; nothing is downloaded.</summary>
public interface IAppUpdateChecker
{
    /// <summary>Reads the newest release.</summary>
    /// <exception cref="AppUpdateException">The releases could not be read, in words a user can act on.</exception>
    Task<AppUpdateResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown when the newest release could not be read.</summary>
public sealed class AppUpdateException : XxsmException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong, in words a user can act on.</param>
    /// <param name="innerException">The underlying failure, when there was one.</param>
    public AppUpdateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The default <see cref="IAppUpdateChecker"/>: GitHub's description of the latest release.</summary>
/// <param name="http">A client that sends XXSM's <c>User-Agent</c>, which GitHub requires.</param>
/// <param name="running">The running version; <see cref="AppInfo.ShortVersion"/> unless a test says otherwise.</param>
/// <param name="source">Where the latest release is described; <see cref="AppInfo.LatestReleaseApiUrl"/> by
/// default.</param>
public sealed class AppUpdateChecker(HttpClient http, string? running = null, Uri? source = null) : IAppUpdateChecker
{
    /// <summary>The largest description read: GitHub's is a few kilobytes.</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly HttpClient _http = http;
    private readonly string _running = running ?? AppInfo.ShortVersion;
    private readonly Uri _source = source ?? new Uri(AppInfo.LatestReleaseApiUrl);

    /// <inheritdoc />
    public async Task<AppUpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        GitHubRelease? release;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _source);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AppUpdateException("No version of XXSM has been published yet, so there is nothing to compare with.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppUpdateException(
                    $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} when asked for the newest XXSM. " +
                    "It limits how often it is asked; XXSM will try again later.");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new AppUpdateException("GitHub's answer about the newest XXSM is far larger than it should be; it was not read.");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[16384];
            int read;

            // Counted as it arrives: an answer that does not stop must not fill memory.
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (bounded.Length + read > MaxBytes)
                {
                    throw new AppUpdateException(
                        "GitHub's answer about the newest XXSM is far larger than it should be; it was not read.");
                }

                bounded.Write(buffer, 0, read);
            }

            bounded.Position = 0;
            release = await JsonSerializer
                .DeserializeAsync(bounded, UpdatesJsonContext.Default.GitHubRelease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                   (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new AppUpdateException($"Could not ask GitHub for the newest XXSM: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new AppUpdateException($"GitHub's answer about the newest XXSM could not be read: {ex.Message}", ex);
        }

        var latest = release?.TagName?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(latest))
        {
            throw new AppUpdateException("GitHub's answer about the newest XXSM names no version.");
        }

        var page = Uri.TryCreate(release!.HtmlUrl, UriKind.Absolute, out var html) && html.Scheme == Uri.UriSchemeHttps
            ? html
            : new Uri(AppInfo.LatestReleaseUrl);

        return new AppUpdateResult(_running, latest, page);
    }
}

/// <summary>The two fields of GitHub's release description that the update check reads.</summary>
public sealed record GitHubRelease
{
    /// <summary>The release's tag, such as <c>v1.0.1</c>.</summary>
    [JsonPropertyName("tag_name")]
    public string? TagName { get; init; }

    /// <summary>The release's page.</summary>
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }
}
