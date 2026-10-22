using System.Text.Json;
using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Loads and saves <see cref="Configuration"/> through the unlocked vault.
/// Falls back to an in-memory temp copy only when the vault is unavailable
/// during early error paths (never persisted without encryption).
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private Configuration? _temp;

    public Configuration Load()
    {
        if (VaultDatabase.Current.IsUnlocked)
        {
            var config = VaultDatabase.Current.LoadConfiguration();
            _temp = null;
            return config;
        }

        if (_temp is not null)
            return Clone(_temp);

        throw new InvalidOperationException(
            "Cannot load LaunchFlow configuration while the encrypted vault is locked.");
    }

    public void Save(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (VaultDatabase.Current.IsUnlocked)
        {
            VaultDatabase.Current.SaveConfiguration(configuration);
            _temp = null;
            return;
        }

        // Keep a transient copy so UI can continue editing if the vault
        // was locked mid-session; it is discarded on restart.
        _temp = Clone(configuration);
    }

    private static Configuration Clone(Configuration source) =>
        JsonSerializer.Deserialize<Configuration>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
}
