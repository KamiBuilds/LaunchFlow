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
        bool includePasswords)
    {
        var package = new PortableExport.PortablePackage
        {
            Config = Clone(configuration),
            Credentials = includePasswords
                ? vault.ExportAll()
                : new Dictionary<Guid, SiteCredential>()
        };
        PortableExport.Write(path, exportPassword, package);
    }

    public static PortableExport.PortablePackage Read(string path, string exportPassword) =>
        PortableExport.Read(path, exportPassword);

    private static Configuration Clone(Configuration source) =>
        JsonSerializer.Deserialize<Configuration>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
}
