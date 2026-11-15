using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>Reject a modal editor result if an import or another edit detached its target.</summary>
public static class WorkflowEditGuard
{
    public static bool IsCurrent(Configuration config, WebsiteWorkflow? selected,
        WebsiteWorkflow original, WorkflowStep originalStep, int index,
        long expectedRevision, long currentRevision) =>
        expectedRevision == currentRevision &&
        ReferenceEquals(selected, original) &&
        config.Workflows.Any(current => ReferenceEquals(current, original)) &&
        index >= 0 && index < original.Steps.Count &&
        ReferenceEquals(original.Steps[index], originalStep);
}
