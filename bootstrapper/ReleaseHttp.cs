using System.Net;
using System.Text.Json;

namespace LaunchFlow.UniversalInstaller;

internal static class ReleaseHttp
{
    // HttpClient must have automatic redirects disabled, so no unvalidated hop is requested.
    internal static async Task<HttpResponseMessage> GetSetupAsync(HttpClient client, Uri url, CancellationToken token)
    {
        for (var hop = 0; hop <= 5; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
                url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
                !(url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                  url.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                  url.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("GitHub returned an unsafe download redirect. Installation is blocked.");
            var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var target = response.Headers.Location;
                response.Dispose();
                if (target is null) throw new InvalidDataException("The setup redirect has no destination.");
                url = target.IsAbsoluteUri ? target : new Uri(url, target);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("The setup download exceeded the redirect limit.");
    }

    internal static async Task<JsonDocument> ReadMetadataAsync(Stream input, CancellationToken token)
    {
        const int maxMetadataBytes = 1_048_576;
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int bytes;
        while ((bytes = await input.ReadAsync(buffer, token)) > 0)
        {
            if (bytes > maxMetadataBytes - output.Length)
                throw new InvalidDataException("GitHub returned unexpectedly large release metadata.");
            output.Write(buffer, 0, bytes);
        }
        try { return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length)); }
        catch (JsonException ex) { throw new InvalidDataException("GitHub returned malformed release metadata.", ex); }
    }
}
