using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Thin wrapper around encrypted vault credentials, with optional KeePassXC lookup.
/// </summary>
public sealed class CredentialVault
{
    public bool Has(Guid workflowId)
    {
        if (EnterprisePolicy.Load().DisableCredentialStorage) return false;
        if (!VaultDatabase.Current.IsUnlocked) return false;
        return VaultDatabase.Current.HasCredential(workflowId);
    }

    public SiteCredential? Read(Guid workflowId)
    {
        if (EnterprisePolicy.Load().DisableCredentialStorage) return null;
        if (!VaultDatabase.Current.IsUnlocked) return null;
        return VaultDatabase.Current.ReadCredential(workflowId);
    }

    /// <summary>
    /// Prefer the LaunchFlow vault; if empty and the workflow references a KeePass
    /// entry path with a configured database, resolve via keepassxc-cli.
    /// </summary>
    public SiteCredential? Read(
        WebsiteWorkflow workflow,
        Configuration? configuration = null,
        string? keepassMasterPassword = null)
    {
        if (EnterprisePolicy.Load().DisableCredentialStorage) return null;
        var local = Read(workflow.Id);
        if (local is not null) return local;

        if (string.IsNullOrWhiteSpace(workflow.KeepassEntryPath))
            return null;

        configuration ??= TryLoadConfig();
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.KeePassDatabasePath))
            return null;

        try
        {
            return KeePassXcService.ReadEntry(
                configuration.KeePassDatabasePath,
                workflow.KeepassEntryPath!,
                configuration.KeePassKeyFilePath,
                keepassMasterPassword);
        }
        catch (Exception)
        {
            // KeePass is optional; vault miss + KeePass failure => no credential.
            return null;
        }
    }

    public void Save(Guid workflowId, SiteCredential credential)
    {
        if (EnterprisePolicy.Load().DisableCredentialStorage)
            throw new InvalidOperationException("Credential storage is disabled by your organization.\");
        VaultDatabase.Current.SaveCredential(workflowId, credential);
    }

    public void Delete(Guid workflowId)
    {
        if (!VaultDatabase.Current.IsUnlocked) return;
        VaultDatabase.Current.DeleteCredential(workflowId);
    }

    public Dictionary<Guid, SiteCredential> ExportAll() =>
        !EnterprisePolicy.Load().DisableCredentialStorage && VaultDatabase.Current.IsUnlocked
            ? VaultDatabase.Current.ExportCredentials()
            : new Dictionary<Guid, SiteCredential>();

    private static Configuration? TryLoadConfig()
    {
        try
        {
            return VaultDatabase.Current.IsUnlocked
                ? VaultDatabase.Current.LoadConfiguration()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
