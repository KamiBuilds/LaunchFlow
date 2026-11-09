using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace LaunchFlow.Services;

/// <summary>
/// Optional check against a GitHub Releases "latest" JSON endpoint.
/// Configure <see cref="Models.Configuration.UpdateFeedUrl"/>; empty disables checks.
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public sealed record UpdateInfo(bool UpdateAvailable, string LatestVersion, string? ReleaseUrl, string Message);

    public static async Task<UpdateInfo> CheckAsync(string? feedUrl, CancellationToken ct = default)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version;
        var currentText = current is null ? AppInfo.VersionString : $"{current.Major}.{current.Minor}.{current.Build}";

        feedUrl = string.IsNullOrWhiteSpace(feedUrl) ? AppInfo.DefaultUpdateFeedUrl : feedUrl;
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return new UpdateInfo(false, currentText, null,
                "Automatic update checks are off. Publish a LaunchFlow GitHub Release to enable update checks, or configure a custom feed under Settings → Security.");
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl.Trim());
            req.Headers.UserAgent.ParseAdd("LaunchFlow-UpdateCheck/" + currentText);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateInfo(false, currentText, null,
                    $"Update check returned HTTP {(int)resp.StatusCode}.");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            var latest = tag.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(latest))
                return new UpdateInfo(false, currentText, htmlUrl, "No release tag found.");

            var newer = IsNewer(latest, currentText);
            return new UpdateInfo(newer, latest, htmlUrl,
                newer
                    ? $"LaunchFlow {latest} is available (you have {currentText})."
                    : $"You're on the latest version ({currentText}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
        {
            CrashLog.Write("UpdateCheck", ex);
            return new UpdateInfo(false, currentText, null, "Could not check for updates: " + ex.Message);
        }
    }

    private static bool IsNewer(string latest, string current)
    {
        if (!Version.TryParse(Normalize(latest), out var l)) return false;
        if (!Version.TryParse(Normalize(current), out var c)) return false;
        return l > c;
    }

    private static string Normalize(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }
}
