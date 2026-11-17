using System.Text.Json;
using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// An isolated synchronous edit transaction. Draft construction never mutates
/// the live model. Storage is finished before the caller may publish changes.
/// The caller provides an ownership check for configuration/edit/session identity.
/// This is deliberately synchronous: no vault key or mutable live model escapes
/// into a background Task.
/// </summary>
public sealed class ConfigurationSaveTransaction
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Action<Configuration, long> _saveDurably;
    private readonly Action<Configuration> _validate;

    public ConfigurationSaveTransaction(
        Action<Configuration, long> saveDurably,
        Action<Configuration> validate)
    {
        _saveDurably = saveDurably ?? throw new ArgumentNullException(nameof(saveDurably));
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
    }

    public SaveTransactionResult Commit(
        Configuration live,
        Action<Configuration> applyDraft,
        Func<bool> owns,
        long vaultGeneration,
        Action<Configuration> publish)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(applyDraft);
        ArgumentNullException.ThrowIfNull(owns);
        ArgumentNullException.ThrowIfNull(publish);

        if (!owns()) return new(SaveTransactionState.SupersededBeforeWrite);

        // JSON roundtrip preserves Configuration/WebsiteWorkflow extension data
        // and all current serializable actions. It never writes plaintext to disk.
        Configuration candidate;
        try
        {
            candidate = JsonSerializer.Deserialize<Configuration>(
                JsonSerializer.Serialize(live, Json), Json)
                ?? throw new InvalidOperationException("Configuration clone failed.");
            applyDraft(candidate);
            _validate(candidate);
        }
        catch (Exception ex)
        {
            return new(SaveTransactionState.ValidationRejected, ex);
        }

        if (!owns()) return new(SaveTransactionState.SupersededBeforeWrite);

        try
        {
            _saveDurably(candidate, vaultGeneration);
        }
        catch (Exception ex)
        {
            return new(SaveTransactionState.StorageRejected, ex);
        }

        // Durable commit cannot be undone by a late UI event. Explicitly
        // distinguish saved-but-superseded from saved-and-published.
        if (!owns()) return new(SaveTransactionState.CommittedButSuperseded);
        try
        {
            // Caller must only assign already validated properties in place;
            // it must not parse input, call OS services or display a dialog.
            publish(candidate);
            return new(SaveTransactionState.CommittedAndPublished);
        }
        catch (Exception ex)
        {
            return new(SaveTransactionState.CommittedButPublicationFailed, ex);
        }
    }
}

public enum SaveTransactionState
{
    ValidationRejected,
    StorageRejected,
    SupersededBeforeWrite,
    CommittedButSuperseded,
    CommittedButPublicationFailed,
    CommittedAndPublished
}

public sealed record SaveTransactionResult(SaveTransactionState State, Exception? Error = null)
{
    public bool Committed => State is SaveTransactionState.CommittedAndPublished
        or SaveTransactionState.CommittedButSuperseded
        or SaveTransactionState.CommittedButPublicationFailed;
    public bool Published => State == SaveTransactionState.CommittedAndPublished;
}
