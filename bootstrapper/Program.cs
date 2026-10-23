using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LaunchFlow.UniversalInstaller;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
    }
}

// Universal distribution:
// - This installer is built as ONE self-contained Windows EXE. Its .NET runtime
//   is included, so users do not install an SDK or a separate runtime.
// - It finds the latest published stable version using GitHub Releases,
//   downloads the matching Setup EXE, verifies the published SHA-256 and size,
//   and launches Windows Setup after the user confirms.
// - Windows Setup includes LaunchFlow, its .NET runtime, and app libraries.
// - Browser automation uses the user's installed Edge or Chrome browser.
//   Additional browsers are not installed silently.
//
// The release contains two EXEs internally, but users download just
// LaunchFlow-Universal-Installer.exe. ZIP archives are not required.

internal sealed record ReleaseInfo(string Tag, string Version, string AssetName, Uri Url, string Sha256, long Size);

internal sealed class InstallerForm : Form
{
    // AppId matches installer/LaunchFlow.iss. Do NOT modify when updating the main app.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}_is1";
    private const long MaxDownloadBytes = 1_073_741_824; // 1 GB hard limit

    private readonly Label _heading = new();
    private readonly Label _status = new();
    private readonly Label _details = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _install = new();
    private readonly Button _refresh = new();
    private readonly Button _open = new();
    private readonly Button _cancel = new();
    private readonly HttpClient _client;
    private readonly string _repository;
    private ReleaseInfo? _release;
    private CancellationTokenSource? _downloadCancellation;
    private bool _busy;

    internal InstallerForm()
    {
        _repository = ReadEmbeddedRepository();
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("LaunchFlow-Universal-Installer/1.0");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        Text = "LaunchFlow — Universal Installer";
        ClientSize = new Size(570, 355);
        MinimumSize = MaximumSize = Size;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(13, 20, 34);
        ForeColor = Color.FromArgb(230, 240, 251);
        Font = new Font("Segoe UI", 10);

        _heading.Text = "LaunchFlow";
        _heading.Font = new Font("Segoe UI Semibold", 23, FontStyle.Bold);
        _heading.SetBounds(28, 22, 510, 50);
        _status.Text = "Checking for the newest release…";
        _status.Font = new Font("Segoe UI Semibold", 12, FontStyle.Bold);
        _status.SetBounds(30, 95, 500, 48);
        _details.Text = "One installer, always the latest stable LaunchFlow. Required .NET runtimes and app libraries are included; no developer tools needed.";
        _details.ForeColor = Color.FromArgb(163, 186, 210);
        _details.SetBounds(30, 152, 505, 64);
        _progress.SetBounds(30, 220, 505, 11);
        _progress.Visible = false;

        ConfigureButton(_install, "Download and install", 30, 261, 214);
        _install.BackColor = Color.FromArgb(37, 176, 164);
        _install.ForeColor = Color.FromArgb(255, 255, 255);
        _install.Click += async (_, _) => await DownloadAndRunAsync();

        ConfigureButton(_refresh, "Check again", 257, 261, 123);
        _refresh.Click += async (_, _) => await CheckLatestAsync();
        ConfigureButton(_open, "Open LaunchFlow", 392, 261, 143);
        _open.Click += (_, _) => OpenInstalledApp();
        ConfigureButton(_cancel, "Cancel download", 30, 261, 214);
        _cancel.Visible = false;
        _cancel.Click += (_, _) => _downloadCancellation?.Cancel();

        Controls.AddRange(new Control[] { _heading, _status, _details, _progress, _install, _refresh, _open, _cancel });
        RefreshButtons();
        Shown += async (_, _) => await CheckLatestAsync();
        FormClosed += (_, _) => { _downloadCancellation?.Cancel(); _client.Dispose(); };
    }

    private static void ConfigureButton(Button button, string label, int x, int y, int width)
    {
        button.Text = label;
        button.SetBounds(x, y, width, 43);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(40, 56, 78);
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    private void RefreshButtons()
    {
        _install.Enabled = !_busy && _release is not null;
        _refresh.Enabled = !_busy;
        _open.Enabled = !_busy && FindInstalledExecutable() is not null;
        _cancel.Visible = _busy && _downloadCancellation is not null;
        _install.Visible = !_cancel.Visible;
    }

    private async Task CheckLatestAsync()
    {
        if (_busy) return;
        _busy = true;
        _release = null;
        _status.Text = "Checking the newest published release…";
        _details.Text = "Connecting securely to GitHub Releases.";
        RefreshButtons();
        try
        {
            if (!Regex.IsMatch(_repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
                _repository.StartsWith("SET-YOUR-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The publisher has not configured a GitHub release repository. The developer must set the repository once before distributing this universal installer.");

            using var response = await _client.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException($"No published stable release was found for {_repository}. Publish LaunchFlow as a GitHub Release first.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("Release has no version tag.");
            var version = tag.TrimStart('v', 'V');
            if (!Version.TryParse(version, out _)) throw new InvalidOperationException("The release tag must be a version like v0.7.9.");

            ReleaseInfo? selected = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.Equals($"LaunchFlow-Setup-v{version}-x64.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rawUrl = asset.GetProperty("browser_download_url").GetString();
                if (rawUrl is null || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) ||
                    url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                    !url.AbsolutePath.StartsWith($"/{_repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("GitHub returned an unexpected download URL. Download cancelled.");
                var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() ?? "" : "";
                if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(digest[7..], "^[0-9a-fA-F]{64}$"))
                    throw new InvalidOperationException("The release has no valid GitHub SHA-256 digest. For safety, installation is blocked.");
                var size = asset.GetProperty("size").GetInt64();
                if (size <= 0 || size > MaxDownloadBytes)
                    throw new InvalidOperationException("The installer has an unexpected file size. Installation is blocked.");
                selected = new ReleaseInfo(tag, version, name, url, digest[7..].ToUpperInvariant(), size);
                break;
            }
            if (selected is null) throw new InvalidOperationException($"Release {tag} does not contain the expected LaunchFlow x64 setup executable.");
            _release = selected;
            var installed = ReadInstalledVersion();
            if (installed is null)
            {
                _status.Text = $"LaunchFlow {version} is available";
                _details.Text = "The latest setup includes LaunchFlow and its required app components. Select Download and install; no separate runtime or SDK is needed.";
            }
            else if (Version.TryParse(installed, out var installedVersion) &&
                     Version.TryParse(version, out var availableVersion) && installedVersion >= availableVersion)
            {
                _status.Text = $"LaunchFlow {installed} is up to date";
                _details.Text = $"Latest published version: {version}. You can open LaunchFlow or reinstall this version if needed.";
                _install.Text = "Reinstall latest";
            }
            else
            {
                _status.Text = $"Update available: {installed} → {version}";
                _details.Text = "The update installs over the existing version. Your encrypted database and workflows stay in their user-data folder.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Couldn't check for updates";
            _details.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _install.Text = _release is not null && ReadInstalledVersion() == _release.Version ? "Reinstall latest" : "Download and install";
            RefreshButtons();
        }
    }

    private async Task DownloadAndRunAsync()
    {
        if (_busy || _release is null) return;
        var release = _release;
        if (MessageBox.Show(this, $"Download LaunchFlow {release.Version} from the configured GitHub Releases repository and run its installer?\n\nRepository: {_repository}",
                "Confirm installation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _busy = true;
        using var cts = new CancellationTokenSource();
        _downloadCancellation = cts;
        _progress.Value = 0;
        _progress.Visible = true;
        _status.Text = "Downloading verified installer…";
        _details.Text = "The SHA-256 digest will be checked before the setup file is allowed to run.";
        RefreshButtons();
        string? pendingPath = null;
        try
        {
            var downloads = Path.Combine(Path.GetTempPath(), "LaunchFlow-VerifiedInstallers");
            Directory.CreateDirectory(downloads);
            pendingPath = Path.Combine(downloads, $"{Guid.NewGuid():N}-{release.AssetName}");
            using var response = await _client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();
            var responseLength = response.Content.Headers.ContentLength;
            if (responseLength.HasValue && responseLength.Value > MaxDownloadBytes)
                throw new InvalidOperationException("The download is unexpectedly large.");

            await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var output = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long copied = 0;
            int bytes;
            while ((bytes = await input.ReadAsync(buffer, cts.Token)) > 0)
            {
                copied += bytes;
                if (copied > MaxDownloadBytes) throw new InvalidOperationException("Maximum download size exceeded.");
                hash.AppendData(buffer, 0, bytes);
                await output.WriteAsync(buffer.AsMemory(0, bytes), cts.Token);
                _progress.Value = Math.Clamp((int)(copied * 100L / release.Size), 0, 100);
                _status.Text = $"Downloading… {_progress.Value}%";
            }
            await output.FlushAsync(cts.Token);
            if (copied != release.Size) throw new InvalidOperationException("The downloaded file is incomplete.");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(release.Sha256)))
                throw new CryptographicException("SHA-256 verification failed. This installer will not run.");
            // Close the file handle BEFORE executing it; Windows cannot start an EXE
            // that is still opened with FileShare.None by this downloader.
            await output.DisposeAsync();
            _status.Text = "Verified — opening setup";
            _details.Text = "Verified against the release's SHA-256 digest. Windows Setup installs LaunchFlow with its bundled runtime and app components.";
            Process.Start(new ProcessStartInfo(pendingPath) { UseShellExecute = true });
            pendingPath = null; // setup may still need to read its file; don't delete it now
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Download cancelled";
            _details.Text = "No installer was launched.";
        }
        catch (Exception ex)
        {
            _status.Text = "Download or verification failed";
            _details.Text = ex.Message;
        }
        finally
        {
            if (pendingPath is not null)
            {
                try { File.Delete(pendingPath); } catch { /* best effort */ }
            }
            _downloadCancellation = null;
            _busy = false;
            _progress.Visible = false;
            RefreshButtons();
        }
    }

    private static string ReadEmbeddedRepository()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LaunchFlow.ReleaseSource");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim().Trim('\uFEFF');
    }

    private static string? ReadInstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("DisplayVersion") as string;
    }

    private static string? FindInstalledExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var raw = key?.GetValue("InstallLocation") as string;
        if (string.IsNullOrWhiteSpace(raw))
            raw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LaunchFlow");
        var exe = Path.Combine(raw, "LaunchFlow.exe");
        return File.Exists(exe) ? exe : null;
    }

    private void OpenInstalledApp()
    {
        var exe = FindInstalledExecutable();
        if (exe is null) return;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to start LaunchFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
