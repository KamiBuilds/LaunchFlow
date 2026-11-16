using LaunchFlow.Services;

if (args.Length == 1 && args[0].StartsWith("--compat-", StringComparison.Ordinal)) return CompatibilityTests.Run(args[0]);

var failures = 0;
var passed = 0;
Run("absent key is unmanaged", () => Check(!EnterprisePolicy.FromProvider(() => null).DisableAutomation));
Run("read error fails closed", () => Denied(EnterprisePolicy.FromProvider(() => throw new IOException("fixture-only"))));
foreach (var name in new[] { "DisableAutomation", "DisableCredentialFill", "DisableCredentialStorage", "DisableExport", "DisableTeach", "RequireHttpsForCredentials" })
{
    Run(name + " DWORD enabled", () =>
    {
        var p = Policy((name, 1));
        var property = name == "DisableTeach" ? "DisableTeachMode" : name;
        Check((bool)typeof(EnterprisePolicy).GetProperty(property)!.GetValue(p)!);
    });
    foreach (var malformed in new object?[] { "1", 2, -1, 1L, true, null, Array.Empty<string>() })
        Run(name + " malformed fails closed", () => Denied(Policy((name, malformed))));
    Run(name + " DWORD zero accepted", () => Check(!Policy((name, 0)).DisableAutomation));
}
Run("unmanaged HTTP/HTTPS allowed but other schemes refused", () =>
{
    Check(Url(Policy(), "http://example.test/path"));
    Check(Url(Policy(), "https://example.test/path"));
    foreach (var url in new[] { "file:///private", "javascript:alert(1)", "about:blank", "not a url", "https://user:secret@example.test/" })
        Check(!Url(Policy(), url));
});
Run("hosts are exact DNS or IP, never substring or wildcard", () =>
{
    var p = Policy(("AllowedHosts", "EXAMPLE.test;192.0.2.5,2001:db8::1"));
    foreach (var url in new[] { "https://example.test/x", "https://192.0.2.5/", "https://[2001:db8::1]/" }) Check(Url(p, url));
    foreach (var url in new[] { "https://sub.example.test/", "https://example.test.evil/", "https://notexample.test/", "https://192.0.2.6/" }) Check(!Url(p, url));
});
foreach (var hosts in new object?[] { "", " ", "*", "*.example.test", "https://example.test", "example.test/path", "example.test:443", "bad_label.test", "a..test", "127.1", "example.test,", ",example.test", "example.test;;other.test", 1, null, new[] { "example.test" } })
    Run("malformed host list denies all", () => { var p = Policy(("AllowedHosts", hosts)); Denied(p); Check(!Url(p, "https://example.test/")); });
Run("provider failure also denies every URL", () => Check(!Url(EnterprisePolicy.FromProvider(() => throw new IOException()), "https://example.test/")));
Run("Current uses a scoped provider and restores unmanaged default", () =>
{
    Check(!EnterprisePolicy.Current.DisableAutomation);
    using (UsePolicy(("DisableAutomation", 1))) Check(EnterprisePolicy.Current.DisableAutomation);
    Check(!EnterprisePolicy.Current.DisableAutomation);
});
Run("Current observes policy changes rather than caching privileges", () =>
{
    var values = new Dictionary<string, object?>();
    using (UseProvider(() => values))
    {
        Check(!EnterprisePolicy.Current.DisableAutomation);
        values["DisableAutomation"] = 1;
        Check(EnterprisePolicy.Current.DisableAutomation);
    }
});
Run("provider matches Windows value names case-insensitively", () => Check(Policy(("disableautomation", 1)).DisableAutomation));
Run("managed empty key leaves unspecified options available", () => Check(!Policy().DisableCredentialStorage && Url(Policy(), "https://example.test/")));
Run("unsupported native platform does not accidentally deny unmanaged usage", () =>
{
    if (!OperatingSystem.IsWindows()) Check(!EnterprisePolicy.Current.DisableCredentialStorage && !EnterprisePolicy.Current.DisableAutomation);
});
VaultTests.Run(Run);
PrivacyTests.Run(Run);
Console.WriteLine($"RESULT passed={passed} failures={failures}");
return failures == 0 ? 0 : 1;

void Run(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("PASS: " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL: " + name + " [" + e.GetType().Name + "]"); }
}
void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
EnterprisePolicy Policy(params (string Name, object? Value)[] values) =>
    EnterprisePolicy.FromProvider(() => values.ToDictionary(v => v.Name, v => v.Value));
bool Url(EnterprisePolicy p, string url)
{
    var method = typeof(EnterprisePolicy).GetMethod("IsUrlAllowed") ?? throw new InvalidOperationException("IsUrlAllowed contract missing");
    return (bool)method.Invoke(p, new object[] { url })!;
}
IDisposable UsePolicy(params (string Name, object? Value)[] values) => UseProvider(() => values.ToDictionary(v => v.Name, v => v.Value));
IDisposable UseProvider(Func<IReadOnlyDictionary<string, object?>?> provider)
{
    var method = typeof(EnterprisePolicy).GetMethod("UseProviderForTesting", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("Scoped provider seam missing");
    return (IDisposable)method.Invoke(null, new object[] { provider })!;
}
void Denied(EnterprisePolicy p) => Check(p.DisableAutomation && p.DisableCredentialFill && p.DisableCredentialStorage && p.DisableExport && p.DisableTeachMode && p.RequireHttpsForCredentials);
