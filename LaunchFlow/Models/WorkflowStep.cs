namespace LaunchFlow.Models;

public sealed class WorkflowStep
{
    public string Type { get; set; } = "Navigate";
    public string Target { get; set; } = "";
    public string? Value { get; set; }
    public string? Description { get; set; }

    // Targets, descriptions and even unknown action types may contain credentials.
    // Editors can present them deliberately; implicit formatting must not disclose them.
    public override string ToString() => Type switch
    {
        "Navigate" => "Navigate",
        "Click" => "Click",
        "Fill" => "Fill",
        "Wait" => "Wait",
        "Press" => "Press",
        _ => "Workflow step"
    };
}
