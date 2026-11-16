using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Prevents modal credential deletion from retargeting after selection changes
/// or configuration replacement. Identity is reference-based, not merely a GUID.
/// </summary>
public static class CredentialDeleteGuard
{
    public static bool IsCurrent(Configuration configuration, WebsiteWorkflow? selected,
        WebsiteWorkflow original, long capturedRevision, long currentRevision, bool authorized)
    {
        return authorized &&
            capturedRevision == currentRevision &&
            ReferenceEquals(selected, original) &&
            configuration.Workflows.Any(w => ReferenceEquals(w, original));
    }
}
