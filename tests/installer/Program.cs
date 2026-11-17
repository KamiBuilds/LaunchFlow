using System.Text.Json;
using LaunchFlow.UniversalInstaller;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Tests = new();

    private static async Task<int> Main()
    {
        Add("selects exact setup from stable release", () =>
        {
            using var metadata = JsonDocument.Parse(Metadata());
            var result = ReleasePolicy.Parse(metadata.RootElement, "Owner/LaunchFlow");
            Equal("0.7.11", result.Version);
            Equal("LaunchFlow-Setup-v0.7.11-x64.exe", result.AssetName);
            Equal(3L, result.Size);
            Equal(new string('A', 64), result.Sha256);
        });

        foreach (var tag in new[] { "vv0.7.11", "V0.7.11", "0.7.11", "v00.7.11", "v0.7", "v0.7.11.0", "v0.7.11-beta", "v2147483648.7.11", "v0.7.11\n" })
            Add($"rejects noncanonical tag {JsonSerializer.Serialize(tag)}", () =>
            {
                var version = tag.TrimStart('v', 'V');
                using var metadata = JsonDocument.Parse(Metadata(tag,
                    $"LaunchFlow-Setup-v{version}-x64.exe"));
                Throws<InvalidDataException>(() => ReleasePolicy.Parse(metadata.RootElement, "Owner/LaunchFlow"));
            });

        foreach (var url in new[] {
            "http://github.com/Owner/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe",
            "https://github.com/Other/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe",
            "https://github.com/Owner/LaunchFlow/releases/download/v0.7.10/LaunchFlow-Setup-v0.7.11-x64.exe",
            "https://github.com/Owner/LaunchFlow/releases/download/v0.7.11/evil.exe",
            "https://github.com:444/Owner/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe",
            "https://user@github.com/Owner/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe",
            "https://github.com/Owner/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe?x=1",
            "https://github.com/Owner/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe#fragment"
        }) Add($"rejects unbound setup URL {url}", () => Reject(Metadata(url: url)));
        foreach (var digest in new[] { "", "sha256:", "sha256:" + new string('g', 64), "sha1:" + new string('a', 64) })
            Add($"rejects invalid digest {digest}", () => Reject(Metadata(digest: digest)));
        foreach (var size in new[] { 0L, -1L, ReleasePolicy.MaxDownloadBytes + 1 })
            Add($"rejects invalid size {size}", () => Reject(Metadata(size: size)));
        Add("rejects draft", () => Reject(Metadata(draft: true)));
        Add("rejects prerelease", () => Reject(Metadata(prerelease: true)));
        foreach (var repository in new[] { "Owner/../repo", "Owner/Repo\n", "./Repo", "Owner/..", "Owner/repo?", "SET-YOUR-OWNER/REPO" })
            Add($"rejects repository {JsonSerializer.Serialize(repository)}", () =>
            {
                using var metadata = JsonDocument.Parse(Metadata());
                Throws<InvalidDataException>(() => ReleasePolicy.Parse(metadata.RootElement, repository));
            });
        foreach (var json in new[] { "{}", "[]", "null", Metadata().Replace("\"tag_name\":\"v0.7.11\"", "\"tag_name\":null"),
            Metadata().Replace("\"size\":3", "\"size\":\"3\""), Metadata().Replace("\"digest\":\"sha256:" + new string('a', 64) + "\"", "\"digest\":null"),
            Metadata().Replace("\"state\":\"uploaded\"", "\"state\":\"open\"") })
            Add($"controlled malformed-metadata error {json}", () => Reject(json));
        Add("rejects duplicate expected setup", () =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Metadata())!;
            var assets = node["assets"]!.AsArray();
            assets.Add(assets[0]!.DeepClone());
            Reject(node.ToJsonString());
        });
        Add("rejects duplicate stable-release fields", () => Reject(Metadata().Replace("{\"tag_name\":", "{\"draft\":true,\"tag_name\":")));
        Add("rejects duplicate asset fields", () => Reject(Metadata().Replace("\"name\":", "\"state\":\"open\",\"name\":")));
        Add("rejects missing expected setup", () => Reject(Metadata(name: "old-setup.exe")));

        Tests.Add(("copies verified bytes with progress", async () =>
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            using var output = new MemoryStream();
            var progress = new List<int>();
            await VerifiedDownload.CopyAsync(input, output, DownloadRelease(), 3, progress.Add, default);
            Equal("010203", Convert.ToHexString(output.ToArray()));
            Equal(100, progress[^1]);
        }));

        foreach (var bytes in new[] { new byte[] { 1, 2 }, new byte[] { 1, 2, 3, 4 }, new byte[] { 3, 2, 1 } })
            Tests.Add(($"rejects unverified bytes {Convert.ToHexString(bytes)}", async () =>
            {
                using var input = new MemoryStream(bytes);
                using var output = new MemoryStream();
                if (bytes.Length == 3)
                    await ThrowsAsync<System.Security.Cryptography.CryptographicException>(() => VerifiedDownload.CopyAsync(input, output, DownloadRelease(), null, null, default));
                else
                    await ThrowsAsync<InvalidDataException>(() => VerifiedDownload.CopyAsync(input, output, DownloadRelease(), null, null, default));
                if (output.Length > 3) throw new Exception("Wrote beyond declared release size.");
            }));
        Tests.Add(("rejects Content-Length mismatch before writing", async () =>
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            using var output = new MemoryStream();
            await ThrowsAsync<InvalidDataException>(() => VerifiedDownload.CopyAsync(input, output, DownloadRelease(), 4, null, default));
            Equal(0L, output.Length);
        }));
        Tests.Add(("verifies unknown Content-Length", async () =>
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            using var output = new MemoryStream();
            await VerifiedDownload.CopyAsync(input, output, DownloadRelease(), null, null, default);
            Equal(3L, output.Length);
        }));
        Tests.Add(("cancellation prevents copy", async () =>
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            using var output = new MemoryStream();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => VerifiedDownload.CopyAsync(input, output, DownloadRelease(), 3, null, cancellation.Token));
            Equal(0L, output.Length);
        }));
        Tests.Add(("rejects oversized metadata before writing", async () =>
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            using var output = new MemoryStream();
            await ThrowsAsync<InvalidDataException>(() => VerifiedDownload.CopyAsync(input, output,
                DownloadRelease() with { Size = ReleasePolicy.MaxDownloadBytes + 1 }, null, null, default));
            Equal(0L, output.Length);
        }));

        Tests.Add(("follows GitHub HTTPS asset redirect", async () =>
        {
            using var handler = new FixtureHandler((uri, index) => index == 1
                ? Redirect("https://release-assets.githubusercontent.com/fixture?signature=synthetic")
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            using var response = await ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default);
            Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Equal(2, handler.Calls);
        }));
        Tests.Add(("reads bounded release metadata", async () =>
        {
            using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Metadata()));
            using var document = await ReleaseHttp.ReadMetadataAsync(input, default);
            Equal("0.7.11", ReleasePolicy.Parse(document.RootElement, "Owner/LaunchFlow").Version);
        }));

        foreach (var target in new[] {
            "http://release-assets.githubusercontent.com/fixture", "https://evil.example/fixture",
            "https://release-assets.githubusercontent.com.evil.example/fixture", "https://user@release-assets.githubusercontent.com/fixture",
            "https://release-assets.githubusercontent.com:444/fixture", "https://github.com/fixture#fragment" })
            Tests.Add(($"blocks unsafe redirect before request {target}", async () =>
            {
                using var handler = new FixtureHandler((_, _) => Redirect(target));
                using var client = new HttpClient(handler);
                await ThrowsAsync<InvalidDataException>(() => ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default));
                Equal(1, handler.Calls);
            }));
        Tests.Add(("rejects redirect loops", async () =>
        {
            using var handler = new FixtureHandler((_, _) => Redirect("https://github.com/loop"));
            using var client = new HttpClient(handler);
            await ThrowsAsync<InvalidDataException>(() => ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default));
            if (handler.Calls > 6) throw new Exception("Exceeded redirect budget.");
        }));
        Tests.Add(("rejects failed setup HTTP status", async () =>
        {
            using var handler = new FixtureHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            using var client = new HttpClient(handler);
            await ThrowsAsync<HttpRequestException>(() => ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default));
        }));
        Tests.Add(("rejects metadata exceeding one MiB", async () =>
        {
            using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"padding\":\"" + new string('x', 1_048_576) + "\"}"));
            await ThrowsAsync<InvalidDataException>(() => ReleaseHttp.ReadMetadataAsync(input, default));
        }));
        Tests.Add(("controlled invalid JSON error", async () =>
        {
            using var input = new MemoryStream(new byte[] { 0xff });
            await ThrowsAsync<InvalidDataException>(() => ReleaseHttp.ReadMetadataAsync(input, default));
        }));

        Tests.Add(("propagates interrupted download without verification", async () =>
        {
            using var input = new FailingReadStream();
            using var output = new MemoryStream();
            await ThrowsAsync<IOException>(() => VerifiedDownload.CopyAsync(input, output, DownloadRelease(), null, null, default));
            Equal(1L, output.Length);
        }));
        Tests.Add(("propagates output write failure", async () =>
        {
            using var input = new MemoryStream(new byte[] { 1, 2, 3 });
            using var output = new FailingWriteStream();
            await ThrowsAsync<IOException>(() => VerifiedDownload.CopyAsync(input, output, DownloadRelease(), null, null, default));
        }));
        Tests.Add(("propagates setup network failure", async () =>
        {
            using var handler = new FixtureHandler((_, _) => throw new HttpRequestException("Synthetic disconnected network."));
            using var client = new HttpClient(handler);
            await ThrowsAsync<HttpRequestException>(() => ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default));
            Equal(1, handler.Calls);
        }));
        Tests.Add(("rejects missing redirect destination", async () =>
        {
            using var handler = new FixtureHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.Found));
            using var client = new HttpClient(handler);
            await ThrowsAsync<InvalidDataException>(() => ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default));
            Equal(1, handler.Calls);
        }));
        Tests.Add(("follows relative GitHub redirect", async () =>
        {
            using var handler = new FixtureHandler((_, index) => index == 1
                ? Redirect("/Owner/LaunchFlow/fixture") : new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            using var client = new HttpClient(handler);
            using var response = await ReleaseHttp.GetSetupAsync(client, DownloadRelease().Url, default);
            Equal(2, handler.Calls);
        }));

        var failed = 0;
        foreach (var test in Tests)
        {
            try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { total = Tests.Count, passed = Tests.Count - failed, failed }));
        return failed == 0 ? 0 : 1;
    }

    // Synthetic metadata and non-executable bytes; never represent a real release.
    private static string Metadata(string tag = "v0.7.11", string? name = null, string? url = null,
        string? digest = null, long size = 3, bool draft = false, bool prerelease = false) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease,
            assets = new[] { new {
                name = name ?? "LaunchFlow-Setup-v0.7.11-x64.exe", state = "uploaded",
                browser_download_url = url ?? "https://github.com/Owner/LaunchFlow/releases/download/v0.7.11/LaunchFlow-Setup-v0.7.11-x64.exe",
                digest = digest ?? "sha256:" + new string('a', 64), size
            } }
        });

    private static HttpResponseMessage Redirect(string url)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
        response.Headers.Location = new Uri(url, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FailingReadStream : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IOException("Synthetic connection reset after one byte.");
            _read = true;
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("Synthetic full destination volume.");
    }

    private sealed class FixtureHandler(Func<Uri, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request.RequestUri!, ++Calls));
        }
    }

    private static ReleaseInfo DownloadRelease() => new("v0.7.11", "0.7.11", "setup.exe",
        new Uri("https://github.com/Owner/LaunchFlow/releases/download/v0.7.11/setup.exe"),
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })), 3);

    private static void Reject(string json)
    {
        using var metadata = JsonDocument.Parse(json);
        Throws<InvalidDataException>(() => ReleasePolicy.Parse(metadata.RootElement, "Owner/LaunchFlow"));
    }

    private static void Add(string name, Action run) => Tests.Add((name, () => { run(); return Task.CompletedTask; }));
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static async Task ThrowsAsync<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
