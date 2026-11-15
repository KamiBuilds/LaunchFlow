using System.IO;
using System.Text.Json;
using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Import/export façade used by MainWindow for .launchflow backups.
/// </summary>
public static class SecureTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    public static void Export(
        string path,
        string exportPassword,
        Configuration configuration,
        CredentialVault vault,
        bool includePasswords,
        long? expectedSession = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(vault);
        var snapshot = vault.CaptureExportSnapshot(includePasswords,
            expectedSession ?? VaultDatabase.Current.SessionGeneration);
        var package = new PortableExport.PortablePackage
        {
            Config = snapshot.Config,
            Credentials = snapshot.Credentials
        };
        // The importer is deliberately stricter than older editable workflows.
        // Check the exact encrypted snapshot *before* opening a destination so
        // export never claims success for a backup that cannot be restored.
        if (package.Config.Workflows?.Any(w => w?.Steps?.Any(s =>
                string.Equals(s?.Type, "Fill", StringComparison.Ordinal)) == true) == true)
            throw new InvalidOperationException(
                "This backup cannot be exported while a website has a Fill action. " +
                "Remove the Fill action from that website's recorded steps and retry. " +
                "Your existing backup was not changed.");
        try
        {
            PortableImportValidation.Validate(package);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException(
                "Cannot export a backup that LaunchFlow could not import. Review the workflow backup format. " +
                " Fix the recorded website settings and retry. Your existing backup was not changed.", ex);
        }
        PortableExport.Write(path, exportPassword, package);
    }

    public static PortableExport.PortablePackage Read(string path, string exportPassword)
    {
        var policy = EnterprisePolicy.Current;
        policy.RequireTransfer();
        // Snapshot import replaces credentials even when the package contains none.
        policy.RequireCredentialStorage();
        var package = PortableExport.Read(path, exportPassword);
        PortableImportValidation.Validate(package);
        return package;
    }

    private static Configuration Clone(Configuration source) =>
        JsonSerializer.Deserialize<Configuration>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
}
