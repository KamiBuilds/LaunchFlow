using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LaunchFlow.UniversalInstaller;

internal static class BootstrapperFormTests
{
    private static async Task Invoke(object target, string name) =>
        await (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null)!;

    private static InstallerForm CreateForm(HttpClient client)
    {
        var form = new InstallerForm();
        typeof(InstallerForm).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, client);
        Process.Reset();
        Process.ChildExit.SetResult();
        Microsoft.Win32.Registry.InstalledVersion = "0.7.19";
        return form;
    }

    private static T Read<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task<int> Main()
    {
        try
        {
            using var handler = new FixtureHttpHandler();
            using var client = new HttpClient(handler);
            var form = CreateForm(client);
            await Invoke(form, "CheckLatestAsync");
            Microsoft.Win32.Registry.InstalledVersion = "0.8.0";
            await Invoke(form, "DownloadAndRunAsync");
            Assert(handler.DownloadCalls == 0, "A newer installation must block a cached older download at entry.");
            Assert(Process.StartCalls == 0, "A newer installation must never launch the cached older setup.");
            Console.WriteLine("PASS upgrade after refresh blocks the cached setup before download");

            form = CreateForm(client);
            handler.BeforePayload = () => Microsoft.Win32.Registry.InstalledVersion = "0.8.0";
            await Invoke(form, "CheckLatestAsync");
            await Invoke(form, "DownloadAndRunAsync");
            Assert(handler.DownloadCalls == 1, "Fixture must upgrade after a permitted download starts.");
            Assert(Process.StartCalls == 0, "An upgrade during download must block older setup at launch.");
            Console.WriteLine("PASS upgrade during download blocks the verified older setup at launch");
            Assert(Read<Label>(form, "_status").Text.Contains("Newer version"), "Blocked download must explain the current installation.");
            Assert(!Read<bool>(form, "_busy"), "Blocked launch must release the form's busy state.");

            handler.BeforePayload = null;
            form = CreateForm(client);
            Microsoft.Win32.Registry.InstalledVersion = "0.8.0";
            var previousDownloads = handler.DownloadCalls;
            await Invoke(form, "CheckLatestAsync");
            await Invoke(form, "DownloadAndRunAsync");
            Assert(handler.DownloadCalls == previousDownloads && Process.StartCalls == 0, "Freshly observed newer version must remain blocked.");
            Console.WriteLine("PASS a fresh newer-version check also blocks download and launch");

            form = CreateForm(client);
            await Invoke(form, "CheckLatestAsync");
            Process.Reset();
            var operation = Invoke(form, "DownloadAndRunAsync");
            string? setupPath = null;
            try
            {
                await Process.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                setupPath = Process.LastPath!;
                Assert(Process.StartCalls == 1 && File.Exists(setupPath), "Same-version reinstall must retain verified fixture bytes during setup.");
                var closing = new ClosingArgs();
                form.RaiseClosing(closing);
                Assert(closing.Cancel && !operation.IsCompleted, "Closing must defer while the setup child owns its lifecycle.");
                Assert(Read<CancellationTokenSource?>(form, "_downloadCancellation") is null, "Downloader cancellation must not claim to cancel a launched setup.");
            }
            finally
            {
                Process.ChildExit.TrySetResult();
                await operation;
            }
            Assert(setupPath is not null && !Directory.Exists(Path.GetDirectoryName(setupPath)), "Staging must be removed only after setup exits.");
            Console.WriteLine("PASS same-version reinstall retains staging and defers close until simulated setup exits");

            form = CreateForm(client);
            await Invoke(form, "CheckLatestAsync");
            handler.BeforePayload = () => Read<CancellationTokenSource>(form, "_downloadCancellation").Cancel();
            await Invoke(form, "DownloadAndRunAsync");
            Assert(Process.StartCalls == 0 && !Read<bool>(form, "_busy"), "Canceled download must not launch setup or retain busy state.");
            Console.WriteLine("PASS download cancellation never launches setup and restores the form state");
            Console.WriteLine("Bootstrapper form behavior tests passed (inert OS/UI seams).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.Message);
            return 1;
        }
    }

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        public int DownloadCalls;
        public Action? BeforePayload { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host == "api.github.com")
            {
                var metadata = JsonSerializer.Serialize(new
                {
                    tag_name = "v0.7.19",
                    draft = false,
                    prerelease = false,
                    assets = new[] { new
                    {
                        name = "LaunchFlow-Setup-v0.7.19-x64.exe", state = "uploaded",
                        browser_download_url = "https://github.com/Owner/LaunchFlow/releases/download/v0.7.19/LaunchFlow-Setup-v0.7.19-x64.exe",
                        digest = "sha256:" + Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), size = 3
                    } }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metadata) });
            }
            DownloadCalls++;
            BeforePayload?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        }
    }
}
