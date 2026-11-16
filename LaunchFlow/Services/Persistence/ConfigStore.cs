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
    private readonly VaultDatabase _vault;
    public ConfigStore(VaultDatabase? vault = null) => _vault = vault ?? VaultDatabase.Current;

    public Configuration Load()
    {
        if (_vault.IsUnlocked)
        {
            var config = _vault.LoadConfiguration();
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
        if (_vault.IsUnlocked)
        {
            _vault.SaveConfiguration(configuration);
            _temp = null;
            return;
        }

        // Keep a transient copy so UI can continue editing if the vault
        // was locked mid-session; it is discarded on restart.
        _temp = Clone(configuration);
    }

    /// <summary>A confirmed workspace save must reach the encrypted database, never the locked-vault temp copy.</summary>
    public void SaveDurable(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!_vault.IsUnlocked)
            throw new InvalidOperationException("The encrypted vault is locked. Workspace changes were not saved.");
        _vault.SaveConfiguration(configuration);
        _temp = null;
    }

    /// <summary>Write only under the same vault session which captured the edit.</summary>
    public void SaveDurable(Configuration configuration, long expectedSession)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _vault.SaveConfiguration(configuration, expectedSession);
        _temp = null;
    }

    public void NoteCommittedExternally() => _temp = null;

    private static Configuration Clone(Configuration source) =>
        JsonSerializer.Deserialize<Configuration>(
            JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;
}
