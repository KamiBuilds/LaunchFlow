using LaunchFlow.Models;

namespace LaunchFlow.Services;

// Only OS/vault seams are replaced. Tests never open a real user's profile or database.
public sealed class CredentialVault
{
    public SiteCredential Credential { get; set; } = new("test-user", "test-only-password");
    public int ReadCount { get; private set; }
    public SiteCredential Read(WebsiteWorkflow workflow, Configuration? configuration, string? master)
    {
        ReadCount++;
        return Credential;
    }
}
