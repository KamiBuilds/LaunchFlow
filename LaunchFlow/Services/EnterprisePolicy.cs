using System.Globalization;
using Microsoft.Win32;

namespace LaunchFlow.Services;

/// <summary>
/// Optional machine-admin policy. Keys under HKLM\SOFTWARE\Policies\LaunchFlow
/// are normally writable only by administrators. No remote service is required.
/// A failure to read an existing policy must not silently grant privileges.
/// </summary>
public sealed class EnterprisePolicy
{
    private const string RegistryPath = @"SOFTWARE\Policies\LaunchFlow";
    public bool DisableAutomation { get; init; }
    public bool DisableCredentialFill { get; init; }
    public bool DisableCredentialStorage { get; init; }
    public bool DisableExport { get; init; }
    public bool DisableTeach { get; init; }
    public bool RequireHttpsForCredentials { get; init; }
    public IReadOnlySet<string> AllowedHosts { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static EnterprisePolicy Load()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: false);
            if (key is null) return new EnterprisePolicy(); // unmanaged personal installations
            static bool Flag(RegistryKey k, string n) =>
                k.GetValue(n) is int v && v == 1;
            var hosts = Convert.ToString(key.GetValue("AllowedHosts"), CultureInfo.InvariantCulture);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(hosts))
            {
                foreach (var part in hosts.Split(new[] { ',', ';' },
                             StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    // Only exact DNS names or IP literals; wildcards are intentionally unsupported.
                    if (Uri.CheckHostName(part) == UriHostNameType.Unknown ||
                        part.Contains('*') || part.Contains('/') || part.Contains(':'))
                        return DenyAll();
                    allowed.Add(part);
                }
            }
            return new EnterprisePolicy
            {
                DisableAutomation = Flag(key, "DisableAutomation"),
                DisableCredentialFill = Flag(key, "DisableCredentialFill"),
                DisableCredentialStorage = Flag(key, "DisableCredentialStorage"),
                DisableExport = Flag(key, "DisableExport"),
                DisableTeach = Flag(key, "DisableTeach"),
                RequireHttpsForCredentials = Flag(key, "RequireHttpsForCredentials"),
                AllowedHosts = allowed
            };
        }
        catch (Exception)
        {
            // Fail closed if the device's managed policy cannot be safely read.
            return DenyAll();
        }
    }

    private static EnterprisePolicy DenyAll() => new()
    {
        DisableAutomation = true,
        DisableCredentialFill = true,
        DisableCredentialStorage = true,
        DisableExport = true,
        DisableTeach = true,
        RequireHttpsForCredentials = true
    };

    public bool AllowsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return false;
        return AllowedHosts.Count == 0 || AllowedHosts.Contains(u.Host);
    }

    public bool AllowsCredentialFill(string url) =>
        !DisableCredentialFill && AllowsUrl(url) &&
        (!RequireHttpsForCredentials ||
         (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
          u.Scheme == Uri.UriSchemeHttps));
}
