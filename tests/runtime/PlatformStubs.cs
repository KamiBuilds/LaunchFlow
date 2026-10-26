using LaunchFlow.Models;

namespace LaunchFlow.Services;

// Only OS/vault seams are replaced. Tests never open a real user's profile or database.
public sealed class CredentialVault
{
    public SiteCredential Credential { get; set; } = new("test-user", "test-only-password");
    public SiteCredential Read(WebsiteWorkflow workflow, Configuration? configuration, string? master) => Credential;
}

public static class MonitorService
{
    public sealed record Bounds(int Left, int Top, int Width, int Height);
    public sealed record Monitor(Bounds Bounds);
    public static List<Monitor> GetMonitors() => new() { new(new(0, 0, 1024, 768)) };
}
