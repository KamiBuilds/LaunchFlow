using System.Net;
using System.Net.Sockets;
using System.Text;
using LaunchFlow.Services;

internal static class UpdateCheckerTests
{
    public static async Task RunAsync()
    {
        Check(!UpdateChecker.TryValidateReleasePageUrl("file:///C:/Windows/notepad.exe", out _),
            "file URI is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl(@"\\server\share\setup.exe", out _),
            "UNC path is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl("javascript:alert(1)", out _),
            "javascript URI is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl("data:text/html,hi", out _),
            "data URI is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl("custom-protocol:open", out _),
            "custom protocol is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl("//evil.test/release", out _),
            "scheme-relative target is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl("https://user:pass@evil.test/release", out _),
            "user-info HTTPS target is rejected");
        Check(!UpdateChecker.TryValidateReleasePageUrl("https://evil.test/release\n", out _),
            "control-character target is rejected");
        Check(UpdateChecker.TryValidateReleasePageUrl(
                "https://github.com/KamiBuilds/LaunchFlow/releases/tag/v9.9.9", out var github) &&
              github!.Scheme == Uri.UriSchemeHttps,
            "legitimate HTTPS release page is accepted");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var feed = $"http://127.0.0.1:{port}/releases/latest";
        var opened = new List<string>();

        async Task ServeOnce(string htmlUrl)
        {
            var clientTask = listener.AcceptTcpClientAsync();
            var checkTask = UpdateChecker.CheckAsync(feed);
            using var client = await clientTask;
            await using var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var body = $$"""{"tag_name":"v9.9.9","html_url":{{System.Text.Json.JsonSerializer.Serialize(htmlUrl)}}}""";
            var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " +
                Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body;
            var bytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(bytes);
            var info = await checkTask;
            Check(info.UpdateAvailable, "newer tag is detected from loopback feed");
            if (info.ReleaseUrl is not null)
            {
                Check(UpdateChecker.TryValidateReleasePageUrl(info.ReleaseUrl, out var safe) && safe is not null,
                    "CheckAsync only publishes validated HTTPS release URLs");
                opened.Add(safe!.AbsoluteUri);
            }
            else
                opened.Add("");
        }

        try
        {
            await ServeOnce("https://github.com/KamiBuilds/LaunchFlow/releases/tag/v9.9.9");
            Check(opened.Last() == "https://github.com/KamiBuilds/LaunchFlow/releases/tag/v9.9.9",
                "GitHub-shaped HTTPS release URL is forwarded");
            opened.Clear();
            await ServeOnce("file:///C:/Windows/notepad.exe");
            Check(opened.Last() == "", "file URI from feed never becomes a shell target");
            opened.Clear();
            await ServeOnce("custom-protocol:open-update");
            Check(opened.Last() == "", "custom protocol from feed never becomes a shell target");
        }
        finally
        {
            listener.Stop();
        }
        Console.WriteLine("UpdateChecker release-url validation tests passed.");
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
