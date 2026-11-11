using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>Bounded non-WPF approve / validate / durable-write / publish flow.
/// The UI still owns presentation; engine and vault retain their own authority.</summary>
public sealed class PasswordOriginApprovalCoordinator(ConfigurationSaveTransaction saveTransaction)
{
    public enum Outcome
    {
        Approved, Declined, Invalidated, StorageRejected,
        CommittedButSuperseded, CommittedButPublicationFailed
    }

    public readonly record struct Decision(Outcome State)
    {
        public bool ApprovedAndPublished => State == Outcome.Approved;
    }

    public Decision Request(
        Configuration liveConfiguration, WebsiteWorkflow workflow,
        string requestedOrigin, Func<bool> stillOwns,
        Func<bool> showNativeConsent, long vaultGeneration)
    {
        ArgumentNullException.ThrowIfNull(liveConfiguration);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(stillOwns);
        ArgumentNullException.ThrowIfNull(showNativeConsent);
        if (!Uri.TryCreate(requestedOrigin, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            !string.Equals(parsed.GetLeftPart(UriPartial.Authority),
                requestedOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return new(Outcome.Invalidated);

        var index = liveConfiguration.Workflows.FindIndex(
            item => ReferenceEquals(item, workflow));
        if (index < 0 || !stillOwns()) return new(Outcome.Invalidated);
        if (!showNativeConsent()) return new(Outcome.Declined);
        if (!stillOwns()) return new(Outcome.Invalidated);

        var committed = saveTransaction.Commit(
            liveConfiguration,
            draft =>
            {
                var target = draft.Workflows[index];
                if (target.Id != workflow.Id)
                    throw new InvalidOperationException("Approval workflow changed.");
                target.PasswordCredentialOrigin = requestedOrigin;
                target.ApprovedLoginOrigins.Add(requestedOrigin);
            },
            stillOwns, vaultGeneration,
            saved =>
            {
                workflow.PasswordCredentialOrigin = saved.Workflows[index].PasswordCredentialOrigin;
                workflow.ApprovedLoginOrigins.Add(requestedOrigin);
            });
        return new(committed.State switch
        {
            SaveTransactionState.CommittedAndPublished => Outcome.Approved,
            SaveTransactionState.CommittedButSuperseded => Outcome.CommittedButSuperseded,
            SaveTransactionState.CommittedButPublicationFailed => Outcome.CommittedButPublicationFailed,
            SaveTransactionState.SupersededBeforeWrite => Outcome.Invalidated,
            _ => Outcome.StorageRejected
        });
    }
}
