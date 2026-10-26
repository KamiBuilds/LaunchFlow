using System.Diagnostics;
using System.IO;
using System.Text;
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

        var args = new StringBuilder();
        args.Append("show -s -a UserName -a Password ");
        if (string.IsNullOrEmpty(masterPassword))
            args.Append("--no-password ");
        if (!string.IsNullOrWhiteSpace(keyFilePath))
        {
            args.Append("-k ");
            args.Append(Quote(keyFilePath));
            args.Append(' ');
        }
        args.Append(Quote(databasePath));
        args.Append(' ');
        args.Append(Quote(entryPath));

        var psi = new ProcessStartInfo
        {
            FileName = cli,
            Arguments = args.ToString(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start keepassxc-cli.");

        if (!string.IsNullOrEmpty(masterPassword))
        {
            process.StandardInput.WriteLine(masterPassword);
            process.StandardInput.Flush();
        }
        process.StandardInput.Close();

        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException(
                "keepassxc-cli timed out. Provide a key file / --no-password database, " +
                "or import the entry into the LaunchFlow vault for unattended automation.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                "keepassxc-cli failed to read the entry. " +
                (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim() +
                " Install KeePassXC, ensure keepassxc-cli works, or save the login in the LaunchFlow vault.");

        return ParseShowOutput(stdout);
    }

    private static SiteCredential ParseShowOutput(string output)
    {
        string? username = null;
        string? password = null;
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = raw.Trim();
            // keepassxc-cli show -a prints attribute values one per line in request order,
            // or "Attribute: value" depending on version.
            if (line.StartsWith("UserName:", StringComparison.OrdinalIgnoreCase))
                username = line["UserName:".Length..].Trim();
            else if (line.StartsWith("Password:", StringComparison.OrdinalIgnoreCase))
                password = line["Password:".Length..].Trim();
            else if (username is null)
                username = line;
            else if (password is null)
                password = line;
        }

        if (password is null)
            throw new InvalidOperationException("KeePass entry did not return a password.");

        return new SiteCredential(username ?? "", password);
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

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
