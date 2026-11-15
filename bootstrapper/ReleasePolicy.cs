using System.Text.Json;
using System.Text.RegularExpressions;

namespace LaunchFlow.UniversalInstaller;

internal sealed record ReleaseInfo(string Tag, string Version, string AssetName, Uri Url, string Sha256, long Size);

internal static class ReleasePolicy
{
    internal const long MaxDownloadBytes = 1_073_741_824;

    internal static void ValidateRepository(string repository)
    {
        if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z") ||
            repository.StartsWith("SET-YOUR-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The publisher must configure a valid OWNER/REPO release source.");
    }

    internal static ReleaseInfo Parse(JsonElement root, string repository)
    {
        ValidateRepository(repository);
        try
        {
            RejectDuplicateFields(root);
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("Only published stable releases can be installed.");
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Regex.IsMatch(tag, @"\Av(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z") ||
                !Version.TryParse(tag[1..], out _))
                throw new InvalidDataException("The stable release tag must be a canonical version such as v0.7.11.");
            var version = tag[1..];
            var name = $"LaunchFlow-Setup-v{version}-x64.exe";
            var matches = root.GetProperty("assets").EnumerateArray().Where(a =>
                a.GetProperty("name").GetString() == name).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException($"Release {tag} must contain exactly one {name}.");
            var asset = matches[0];
            if (asset.GetProperty("state").GetString() != "uploaded")
                throw new InvalidDataException("The setup asset is not fully uploaded.");
            var rawUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
            var prefix = $"https://github.com/{repository}/releases/download/";
            // Bind the URL to this repository, tag AND filename, not merely a path prefix.
            if (!rawUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                rawUrl[prefix.Length..] != $"{tag}/{name}")
                throw new InvalidDataException("The release contains an unexpected setup URL.");
            var digest = asset.GetProperty("digest").GetString() ?? "";
            if (!Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("The release has no valid GitHub SHA-256 digest. Installation is blocked.");
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaxDownloadBytes)
                throw new InvalidDataException("The setup asset has an unexpected size. Installation is blocked.");
            return new ReleaseInfo(tag, version, name, new Uri(rawUrl), digest[7..].ToUpperInvariant(), size);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The release metadata is incomplete or malformed. Installation is blocked.", ex);
        }
    }

    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // JSON consumers disagree on duplicate names; never authorize ambiguous metadata.
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException("The release metadata contains duplicate fields. Installation is blocked.");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateFields(item);
        }
    }
}
