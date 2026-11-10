using System.Diagnostics;
using System.IO;
using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Reads a KeePassXC .kdbx entry via <c>keepassxc-cli show</c>.
/// Requires keepassxc-cli on PATH (or KeePassXC installation folder).
/// </summary>
public static class KeePassXcService
{
    public static bool IsCliAvailable() => ResolveCliPath() is not null;

    public sealed record DatabaseChoice(string Path, string Display);

    // Look for .kdbx filenames in common folders without opening their contents.
    public static IReadOnlyList<DatabaseChoice> FindDatabases()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(home, "Downloads"),
            Path.Combine(home, "KeePassXC"),
            Path.Combine(home, "Passwords"),
            Environment.GetEnvironmentVariable("OneDrive"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetEnvironmentVariable("OneDriveConsumer")
        };

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            directories.Add(root);
            foreach (var name in new[] { "Documents", "KeePassXC", "KeePass", "Passwords" })
            {
                var subfolder = Path.Combine(root, name);
                if (Directory.Exists(subfolder)) directories.Add(subfolder);
            }
        }

        var results = new Dictionary<string, DatabaseChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.kdbx", SearchOption.TopDirectoryOnly).Take(40))
                    results.TryAdd(file, new DatabaseChoice(file,
                        $"{Path.GetFileName(file)} — {Path.GetDirectoryName(file)}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return results.Values.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // List KeePassXC entry names; send the master password only through stdin.
    public static async Task<IReadOnlyList<string>> ListEntriesAsync(
        string databasePath, string? keyFilePath, string masterPassword)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Choose a valid KeePassXC database first.");

        var cli = ResolveCliPath()
            ?? throw new InvalidOperationException("KeePassXC is not installed.");

        var start = new ProcessStartInfo(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("ls");
        start.ArgumentList.Add("-R");
        start.ArgumentList.Add("-f");
        if (string.IsNullOrEmpty(masterPassword)) start.ArgumentList.Add("--no-password");
        if (!string.IsNullOrWhiteSpace(keyFilePath))
        {
            start.ArgumentList.Add("-k");
            start.ArgumentList.Add(keyFilePath);
        }
        start.ArgumentList.Add(databasePath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start KeePassXC.");

        if (!string.IsNullOrEmpty(masterPassword))
        {
            await process.StandardInput.WriteLineAsync(masterPassword);
            await process.StandardInput.FlushAsync();
        }
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    "KeePassXC couldn't unlock the database. Check your master password and key file.");

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => !line.EndsWith('/') &&
                               !line.StartsWith("Enter password", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(1000)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("KeePassXC took too long to open. Try again.");
        }
    }


    public static SiteCredential ReadEntry(string databasePath, string entryPath, string? keyFilePath = null, string? masterPassword = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            throw new FileNotFoundException("KeePass database not found.", databasePath);
        if (string.IsNullOrWhiteSpace(entryPath))
            throw new ArgumentException("KeePass entry path is required.", nameof(entryPath));

        var cli = ResolveCliPath()
                  ?? throw new InvalidOperationException(
                      "keepassxc-cli was not found. Install KeePassXC and ensure keepassxc-cli is on PATH, " +
                      "or import credentials into the LaunchFlow vault instead.");

        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "show", "-s", "-a", "UserName", "-a", "Password" })
            psi.ArgumentList.Add(argument);
        if (string.IsNullOrEmpty(masterPassword)) psi.ArgumentList.Add("--no-password");
        if (!string.IsNullOrWhiteSpace(keyFilePath))
        {
            psi.ArgumentList.Add("-k");
            psi.ArgumentList.Add(keyFilePath);
        }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(databasePath);
        psi.ArgumentList.Add(entryPath);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start keepassxc-cli.");

        if (!string.IsNullOrEmpty(masterPassword))
        {
            process.StandardInput.WriteLine(masterPassword);
            process.StandardInput.Flush();
        }
        process.StandardInput.Close();

        // Drain both pipes before waiting; a verbose CLI can fill stderr and block its own exit.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException(
                "keepassxc-cli timed out. Provide a key file / --no-password database, " +
                "or import the entry into the LaunchFlow vault for unattended automation.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "KeePassXC could not read the entry. Check the database, entry name, master password and key file.");

        return ParseShowOutput(stdout);
    }

    private static SiteCredential ParseShowOutput(string output)
    {
        // Explicit -a attributes are raw values in request order, not labeled fields.
        // Trimming or removing empty lines changes valid credentials, especially an empty username.
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2 || lines.Length > 3 || (lines.Length == 3 && lines[2].Length != 0))
            throw new InvalidOperationException("KeePass returned an unsupported multiline credential.");
        return new SiteCredential(lines[0], lines[1]);
    }

    private static string? ResolveCliPath()
    {
        var candidates = new[]
        {
            "keepassxc-cli",
            "keepassxc-cli.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "KeePassXC", "keepassxc-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "KeePassXC", "keepassxc-cli.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "KeePassXC", "keepassxc-cli.exe")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.Contains(Path.DirectorySeparatorChar) || candidate.Contains(Path.AltDirectorySeparatorChar))
                {
                    if (File.Exists(candidate)) return candidate;
                    continue;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = candidate,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                var path = process?.StandardOutput.ReadLine()?.Trim();
                process?.WaitForExit(5000);
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
            catch
            {
                // try next candidate
            }
        }

        return null;
    }

}
