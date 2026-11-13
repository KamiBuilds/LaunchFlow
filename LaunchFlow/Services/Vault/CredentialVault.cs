using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Thin wrapper around encrypted vault credentials, with optional KeePassXC lookup.
/// </summary>
public sealed class CredentialVault
{
    public bool Has(Guid workflowId)
    {
        EnterprisePolicy.Current.RequireCredentialStorage();
        if (!VaultDatabase.Current.IsUnlocked) return false;
        return VaultDatabase.Current.HasCredential(workflowId);
    }

    public SiteCredential? Read(Guid workflowId)
    {
        EnterprisePolicy.Current.RequireCredentialRead();
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
        EnterprisePolicy.Current.RequireCredentialRead();
        // A slow KeePass lookup must retain its exact vault authorization:
        // lock + unlock is a new session even if the vault is open again.
        var database = VaultDatabase.Current;
        var authorizedSession = database.SessionGeneration;
        if (!database.IsUnlocked) return null;
        var local = Read(workflow.Id);
        if (local is not null)
            return database.IsUnlocked && database.SessionGeneration == authorizedSession
                ? local : null;

        if (string.IsNullOrWhiteSpace(workflow.KeepassEntryPath))
            return null;

        configuration ??= TryLoadConfig();
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.KeePassDatabasePath))
            return null;

        if (!database.IsUnlocked || database.SessionGeneration != authorizedSession) return null;
        try
        {
            var credential = KeePassXcService.ReadEntry(
                configuration.KeePassDatabasePath,
                workflow.KeepassEntryPath!,
                configuration.KeePassKeyFilePath,
                keepassMasterPassword);
            // Administrator policy and the vault session can both change while
            // the CLI runs. Reauthorize before releasing an external secret.
            EnterprisePolicy.Current.RequireCredentialRead();
            return database.IsUnlocked && database.SessionGeneration == authorizedSession
                ? credential : null;
        }
        catch (Exception)
        {
            // KeePass is optional; vault miss + KeePass failure => no credential.
            return null;
        }
    }

    public void Save(Guid workflowId, SiteCredential credential)
    {
        VaultDatabase.Current.SaveCredential(workflowId, credential);
    }

    public void SaveWithConfiguration(Guid workflowId, SiteCredential credential,
        Configuration configuration, long expectedSession) =>
        VaultDatabase.Current.SaveCredentialAndConfiguration(workflowId, credential, configuration, expectedSession);

    public void Delete(Guid workflowId)
    {
        if (!VaultDatabase.Current.IsUnlocked) return;
        VaultDatabase.Current.DeleteCredential(workflowId);
    }

    public void DeleteWithConfiguration(Guid workflowId, Configuration configuration, long expectedSession) =>
        VaultDatabase.Current.DeleteCredentialAndConfiguration(workflowId, configuration, expectedSession);

    public void DeleteWorkflowWithConfiguration(Guid workflowId, Configuration configurationWithoutWorkflow,
        long expectedSession) =>
        VaultDatabase.Current.DeleteWorkflowAndCredential(workflowId, configurationWithoutWorkflow, expectedSession);

    public Dictionary<Guid, SiteCredential> ExportAll()
    {
        var policy = EnterprisePolicy.Current;
        policy.RequireTransfer();
        policy.RequireCredentialStorage();
        return VaultDatabase.Current.ExportCredentials();
    }

    public (Configuration Config, Dictionary<Guid, SiteCredential> Credentials) CaptureExportSnapshot(
        bool includePasswords, long expectedSession) =>
        VaultDatabase.Current.CaptureExportSnapshot(includePasswords, expectedSession);

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
