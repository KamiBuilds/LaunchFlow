using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
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

// Both installer layers include .NET; browser setup is an explicit app-managed choice.
internal sealed class InstallerForm : Form
{
    // AppId matches installer/LaunchFlow.iss. Do NOT modify when updating the main app.
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B1A7C15-093B-4DBA-BDA7-BF43A2E67320}_is1";

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
    private bool _newerInstalled;

    internal InstallerForm()
    {
        _repository = ReadEmbeddedRepository();
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
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
        ConfigureButton(_cancel, "Cancel", 30, 261, 214);
        _cancel.Visible = false;
        _cancel.Click += (_, _) => _downloadCancellation?.Cancel();

        Controls.AddRange(new Control[] { _heading, _status, _details, _progress, _install, _refresh, _open, _cancel });
        RefreshButtons();
        Shown += async (_, _) => await CheckLatestAsync();
        FormClosing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            _downloadCancellation?.Cancel();
        };
        FormClosed += (_, _) => _client.Dispose();
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
        _install.Enabled = !_busy && _release is not null && !_newerInstalled;
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
        _newerInstalled = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _downloadCancellation = cts;
        _status.Text = "Checking the newest published release…";
        _details.Text = "Connecting securely to GitHub Releases.";
        RefreshButtons();
        try
        {
            ReleasePolicy.ValidateRepository(_repository);
            using var response = await _client.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException($"No published stable release was found for {_repository}.");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var document = await ReleaseHttp.ReadMetadataAsync(stream, cts.Token);
            _release = ReleasePolicy.Parse(document.RootElement, _repository);
            var version = _release.Version;
            var installed = ReadInstalledVersion();
            if (installed is null)
            {
                _status.Text = $"LaunchFlow {version} is available";
                _details.Text = "The latest setup includes LaunchFlow and its required app components. Select Download and install; no separate runtime or SDK is needed.";
            }
            else if (Version.TryParse(installed, out var installedVersion) &&
                     Version.TryParse(version, out var availableVersion) && installedVersion >= availableVersion)
            {
                _newerInstalled = installedVersion > availableVersion;
                _status.Text = _newerInstalled ? $"Newer version {installed} is installed" : $"LaunchFlow {installed} is up to date";
                _details.Text = _newerInstalled
                    ? $"Published version: {version}. Downgrade is blocked to protect newer data formats."
                    : $"Latest published version: {version}. You can open LaunchFlow or reinstall this version if needed.";
            }
            else
            {
                _status.Text = $"Update available: {installed} → {version}";
                _details.Text = "The update installs over the existing version. Your encrypted database and workflows stay in their user-data folder.";
            }
        }
        catch (OperationCanceledException)
        {
            _release = null;
            _status.Text = "Update check cancelled or timed out";
            _details.Text = "No installer was downloaded. Check again when ready.";
        }
        catch (Exception ex)
        {
            _release = null;
            _status.Text = "Couldn't check for updates";
            _details.Text = ex.Message;
        }
        finally
        {
            _downloadCancellation = null;
            _busy = false;
            _install.Text = _release is not null && ReadInstalledVersion() == _release.Version ? "Reinstall latest" : "Download and install";
            RefreshButtons();
        }
    }

    private async Task DownloadAndRunAsync()
    {
        if (_busy || _release is null || _newerInstalled) return;
        var release = _release;
        if (RefuseDowngrade(release)) return;
        if (MessageBox.Show(this, $"Download LaunchFlow {release.Version} from the configured GitHub Releases repository and run its installer?\n\nRepository: {_repository}",
                "Confirm installation", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _busy = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _downloadCancellation = cts;
        _progress.Value = 0;
        _progress.Visible = true;
        _status.Text = "Downloading verified installer…";
        _details.Text = "The SHA-256 digest will be checked before the setup file is allowed to run.";
        RefreshButtons();
        string? stagingDirectory = null;
        try
        {
            stagingDirectory = Directory.CreateTempSubdirectory("LaunchFlow-Setup-").FullName;
            var pendingPath = Path.Combine(stagingDirectory, release.AssetName);
            using var response = await ReleaseHttp.GetSetupAsync(_client, release.Url, cts.Token);
            await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
            await using (var output = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
            {
                await VerifiedDownload.CopyAsync(input, output, release, response.Content.Headers.ContentLength,
                    value => { _progress.Value = value; _status.Text = $"Downloading… {value}%"; }, cts.Token);
            }
            cts.Token.ThrowIfCancellationRequested();
            // Another installer may finish while this window is open or while the download awaits I/O.
            if (RefuseDowngrade(release)) return;
            _status.Text = "Verified — Windows Setup is running";
            _details.Text = "Finish or cancel Windows Setup to return here. LaunchFlow includes its runtime and app components; browser setup requires your choice in the app.";
            _downloadCancellation = null;
            RefreshButtons();
            using var setup = Process.Start(new ProcessStartInfo(pendingPath) { UseShellExecute = true })
                ?? throw new InvalidOperationException("Windows could not launch the verified setup.");
            // Once launched, cancellation belongs to Windows Setup, not the downloader.
            await setup.WaitForExitAsync();
            _status.Text = setup.ExitCode == 0 ? "Windows Setup completed" : "Windows Setup did not complete";
            _details.Text = setup.ExitCode == 0
                ? "Open LaunchFlow to continue. No developer SDK or separate .NET install is required."
                : $"Setup returned exit code {setup.ExitCode}. Your existing user-data folder was not removed by this downloader.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Download cancelled or timed out";
            _details.Text = "No installer was launched.";
        }
        catch (Exception ex)
        {
            _status.Text = "Download or verification failed";
            _details.Text = ex.Message;
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch (IOException) { /* Windows may still hold an installer file. */ }
                catch (UnauthorizedAccessException) { /* Cleanup must not mask the installation result. */ }
            }
            _downloadCancellation = null;
            _busy = false;
            _progress.Visible = false;
            RefreshButtons();
        }
    }

    private bool RefuseDowngrade(ReleaseInfo release)
    {
        var installed = ReadInstalledVersion();
        if (!Version.TryParse(installed, out var installedVersion) || installedVersion <= Version.Parse(release.Version))
            return false;
        _newerInstalled = true;
        _status.Text = $"Newer version {installed} is installed";
        _details.Text = $"Published version: {release.Version}. Downgrade is blocked to protect newer data formats.";
        RefreshButtons();
        return true;
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
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static string? FindInstalledExecutable()
    {
        string? raw = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            raw = key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
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
