using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LaunchFlow.Services;

/// <summary>Machine-administrator policy, separate from user configuration.</summary>
public sealed class EnterprisePolicy
{
    private const string RegistryPath = @"SOFTWARE\Policies\LaunchFlow";
    private static readonly AsyncLocal<Func<IReadOnlyDictionary<string, object?>?>?> TestProvider = new();

    // Unsupported platforms do not attempt Registry access or infer Windows enrollment.
    public static EnterprisePolicy Current => TestProvider.Value is { } provider
        ? FromProvider(provider)
        : OperatingSystem.IsWindows() ? FromProvider(ReadWindowsPolicy) : new();

    private EnterprisePolicy() { }

    internal static IDisposable UseProviderForTesting(Func<IReadOnlyDictionary<string, object?>?> provider)
    {
        var previous = TestProvider.Value;
        TestProvider.Value = provider;
        return new ProviderScope(previous);
    }

    private sealed class ProviderScope(Func<IReadOnlyDictionary<string, object?>?>? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            TestProvider.Value = previous;
            _disposed = true;
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyDictionary<string, object?>? ReadWindowsPolicy()
    {
        // Fixed 64-bit HKLM view: x86/x64 processes must see the same administrator authority.
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RegistryPath, writable: false);
        if (key is null) return null; // Only a truly absent key is unmanaged on Windows.
        var names = new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "DisableAutomation", "DisableCredentialFill", "DisableCredentialStorage", "DisableExport", "DisableTeach", "RequireHttpsForCredentials", "AllowedHosts" })
        {
            if (!names.Contains(name)) continue;
            var kind = key.GetValueKind(name);
            var expected = name == "AllowedHosts" ? RegistryValueKind.String : RegistryValueKind.DWord;
            if (kind != expected) throw new InvalidOperationException("Invalid administrator policy.");
            values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        return values;
    }
    public bool DisableAutomation { get; private init; }
    public bool DisableCredentialFill { get; private init; }
    public bool DisableCredentialStorage { get; private init; }
    public bool DisableExport { get; private init; }
    public bool DisableTeachMode { get; private init; }
    public bool RequireHttpsForCredentials { get; private init; }
    private bool DenyUrls { get; init; }
    private HashSet<string>? Hosts { get; init; }

    internal static EnterprisePolicy FromProvider(Func<IReadOnlyDictionary<string, object?>?> provider)
    {
        try
        {
            var provided = provider();
            if (provided is null) return new();
            var values = new Dictionary<string, object?>(provided, StringComparer.OrdinalIgnoreCase);
            bool Flag(string name)
            {
                if (!values.TryGetValue(name, out var value)) return false;
                if (value is not int number || (number != 0 && number != 1))
                    throw new InvalidOperationException("Invalid administrator policy.");
                return number == 1;
            }
            HashSet<string>? hosts = null;
            if (values.TryGetValue("AllowedHosts", out var raw))
            {
                if (raw is not string text || string.IsNullOrWhiteSpace(text))
                    throw new InvalidOperationException("Invalid administrator policy.");
                hosts = new(StringComparer.OrdinalIgnoreCase);
                foreach (var part in text.Split(new[] { ',', ';' }))
                {
                    if (!TryHost(part.Trim(), out var host))
                        throw new InvalidOperationException("Invalid administrator policy.");
                    hosts.Add(host);
                }
            }
            return new()
            {
                DisableAutomation = Flag("DisableAutomation"),
                DisableCredentialFill = Flag("DisableCredentialFill"),
                DisableCredentialStorage = Flag("DisableCredentialStorage"),
                DisableExport = Flag("DisableExport"),
                DisableTeachMode = Flag("DisableTeach"),
                RequireHttpsForCredentials = Flag("RequireHttpsForCredentials"),
                Hosts = hosts
            };
        }
        catch
        {
            return new()
            {
                DisableAutomation = true, DisableCredentialFill = true,
                DisableCredentialStorage = true, DisableExport = true,
                DisableTeachMode = true, RequireHttpsForCredentials = true, DenyUrls = true
            };
        }
    }

    internal void RequireCredentialStorage()
    {
        if (DisableCredentialStorage)
            throw new InvalidOperationException("Credential storage and access are disabled by your organization.");
    }

    internal void RequireCredentialRead()
    {
        RequireCredentialStorage();
        if (DisableCredentialFill)
            throw new InvalidOperationException("Credential filling is disabled by your organization.");
    }

    internal void RequireTransfer()
    {
        if (DisableExport)
            throw new InvalidOperationException("Portable imports and exports are disabled by your organization.");
    }

    public bool IsUrlAllowed(string url)
    {
        if (DenyUrls || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !TryHost(uri.Host.Trim('[', ']'), out var host)) return false;
        return Hosts is null || Hosts.Contains(host);
    }

    private static bool TryHost(string value, out string host)
    {
        host = "";
        if (string.IsNullOrEmpty(value)) return false;
        if (IPAddress.TryParse(value, out var ip))
        {
            // IPAddress accepts legacy shorthand, integer and octal IPv4. Policy does not.
            if (ip.AddressFamily == AddressFamily.InterNetwork &&
                (value.Split('.').Length != 4 || !string.Equals(value, ip.ToString(), StringComparison.Ordinal)))
                return false;
            if (value.Contains('%')) return false; // no interface-scoped IPv6
            host = ip.ToString();
            return true;
        }
        if (value.Length > 253 || value.EndsWith('.') || value.All(c => char.IsAsciiDigit(c) || c == '.')) return false;
        foreach (var label in value.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        }
        host = value.ToLowerInvariant();
        return true;
    }
}
