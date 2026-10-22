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
