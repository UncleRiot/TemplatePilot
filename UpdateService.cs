using System.Net;
using System.Net.Http;
using System.Text.Json;
using TemplatePilot.Infrastructure;

namespace TemplatePilot.Services;

public sealed record UpdateCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    string LatestVersion,
    string ReleaseUrl);

public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/UncleRiot/TemplatePilot";
    public const string ReleasesUrl = "https://github.com/UncleRiot/TemplatePilot/releases";

    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/UncleRiot/TemplatePilot/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly LoggingService _logging;

    public UpdateService(LoggingService logging)
    {
        _logging = logging;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"TemplatePilot/{AppConstants.AppVersion}");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add(
            "X-GitHub-Api-Version",
            "2022-11-28");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(
                    LatestReleaseApiUrl,
                    cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    false,
                    false,
                    string.Empty,
                    ReleasesUrl);
            }

            response.EnsureSuccessStatusCode();

            await using Stream stream =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken).ConfigureAwait(false);

            using JsonDocument document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

            string tagName = document.RootElement
                .GetProperty("tag_name")
                .GetString() ?? string.Empty;

            string releaseUrl = document.RootElement
                .GetProperty("html_url")
                .GetString() ?? ReleasesUrl;

            if (!TryParseVersion(tagName, out Version? latestVersion) ||
                !TryParseVersion(AppConstants.AppVersion, out Version? currentVersion))
            {
                _logging.Warning(
                    $"Update version could not be parsed. Current='{AppConstants.AppVersion}', Latest='{tagName}'.");
                return new UpdateCheckResult(
                    false,
                    false,
                    tagName,
                    releaseUrl);
            }

            return new UpdateCheckResult(
                true,
                latestVersion!.CompareTo(currentVersion) > 0,
                latestVersion.ToString(),
                releaseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logging.Warning("Update check timed out.");
            return new UpdateCheckResult(
                false,
                false,
                string.Empty,
                ReleasesUrl);
        }
        catch (HttpRequestException exception)
        {
            _logging.Warning("Update check failed.", exception);
            return new UpdateCheckResult(
                false,
                false,
                string.Empty,
                ReleasesUrl);
        }
        catch (JsonException exception)
        {
            _logging.Warning("Update response could not be read.", exception);
            return new UpdateCheckResult(
                false,
                false,
                string.Empty,
                ReleasesUrl);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static bool TryParseVersion(
        string value,
        out Version? version)
    {
        string normalized = value.Trim();

        if (normalized.StartsWith(
            "v",
            StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        int suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return Version.TryParse(normalized, out version);
    }
}
